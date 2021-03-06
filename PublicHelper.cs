﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web;
using System.Xml.Linq;
using Microsoft.Win32;
using Mygod.Net;
using Mygod.Xml.Linq;

namespace Mygod.Skylark
{
    public static partial class Helper
    {
        public const string Unknown = "未知";

        static Helper()
        {
            var mimeMappingType = Assembly.GetAssembly(typeof(HttpRuntime)).GetType("System.Web.MimeMapping");
            if (mimeMappingType == null) throw new SystemException("Couldn't find MimeMapping type");
            GetMimeMappingMethodInfo = mimeMappingType.GetMethod("GetMimeMapping",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (GetMimeMappingMethodInfo == null) throw new SystemException("Couldn't find GetMimeMapping method");
            if (GetMimeMappingMethodInfo.ReturnType != typeof(string))
                throw new SystemException("GetMimeMapping method has invalid return type");
            if (GetMimeMappingMethodInfo.GetParameters().Length != 1
                && GetMimeMappingMethodInfo.GetParameters()[0].ParameterType != typeof(string))
                throw new SystemException("GetMimeMapping method has invalid parameters");
        }

        private static readonly object Locker = new object();
        private static readonly MethodInfo GetMimeMappingMethodInfo;
        public static string GetMimeType(string fileName)
        {
            lock (Locker) return (string)GetMimeMappingMethodInfo.Invoke(null, new object[] { fileName });
        }

        public static string GetDefaultExtension(string mimeType)
        {
            try
            {
                var key = Registry.ClassesRoot.OpenSubKey(@"MIME\Database\Content Type\" + mimeType, false);
                var value = key != null ? key.GetValue("Extension", null) : null;
                return value != null ? value.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        public static string GetMime(string contentType)
        {
            try
            {
                return contentType.Split(';')[0]; // kill that "; charset=utf-8" stupid stuff
            }
            catch
            {
                return contentType;
            }
        }

        public static string Shorten(this DateTime value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Ticks.ToString(CultureInfo.InvariantCulture)));
        }

        public static DateTime Deshorten(string value)
        {
            return new DateTime(long.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value))), DateTimeKind.Utc);
        }
    }

    public static partial class FileHelper
    {
        public static string Combine(params string[] paths)
        {
            var result = string.Empty;
            foreach (var path in paths.Select(path => path.Trim('/')))
            {
                if (!string.IsNullOrEmpty(result)) result += '/';
                result += path;
            }
            return result;
        }

        public static void WriteAllText(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);
        }

        public static XElement GetElement(string path)
        {
            try
            {
                return XHelper.Load(path).Root;
            }
            catch
            {
                return null;
            }
        }
        public static string GetFileValue(string path, string attribute)
        {
            try
            {
                return GetElement(path).GetAttributeValue(attribute);
            }
            catch
            {
                return null;
            }
        }
        public static void SetFileValue(string path, string attribute, string value)
        {
            XDocument doc;
            try
            {
                doc = XHelper.Load(path);
            }
            catch
            {
                doc = new XDocument(new XElement("file"));
            }
            var root = doc.Element("file");
            root.SetAttributeValue(attribute, value);
            doc.Save(path);
        }

        public static string GetState(string dataPath)
        {
            return GetFileValue(dataPath, "state");
        }
        public static bool IsReady(string dataPath)
        {
            return GetState(dataPath) == TaskType.NoTask;
        }
        public static void WaitForReady(string dataPath, int timeoutSeconds = -1)
        {
            while (!IsReady(dataPath))
            {
                if (timeoutSeconds-- == 0) throw new TimeoutException();
                Thread.Sleep(1000);
            }
        }

        public static bool? IsFileExtended(string path)
        {
            if (File.Exists(path)) return true;
            if (Directory.Exists(path)) return false;
            return null;
        }
        public static bool IsFile(string path)
        {
            var result = IsFileExtended(path);
            if (result.HasValue) return result.Value;
            throw new FileNotFoundException();
        }
        public static void DeleteWithRetries(string path)
        {
        retry:
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
                Thread.Sleep(100);
                goto retry;
            }
        }
        public static void DeleteWithRetries(IEnumerable<string> paths)
        {
            foreach (var path in paths) DeleteWithRetries(path);
        }
        public static void CancelControl(string dataPath)
        {
            if (Directory.Exists(dataPath))
            {
                foreach (var stuff in Directory.EnumerateFileSystemEntries(dataPath)) CancelControl(stuff);
                return;
            }
            if (!File.Exists(dataPath) || !dataPath.EndsWith(".data", true, CultureInfo.InvariantCulture))
                return; // ignore non-data files
            var element = GetElement(dataPath);
            if (element.GetAttributeValue("state") == TaskType.NoTask) return;
            CloudTask.KillProcessTree(element.GetAttributeValueWithDefault<int>("pid"));
        }
        public static void Delete(string path)
        {
            var filePath = GetFilePath(path);
            var isFile = IsFileExtended(filePath);
            if (!isFile.HasValue) return;
            var dataPath = isFile.Value ? GetDataFilePath(path) : GetDataPath(path);
            CancelControl(dataPath);
            DeleteWithRetries(GetFilePath(path));
            DeleteWithRetries(dataPath);
            if (!isFile.Value) return;
            string dirPath = GetDataPath(Path.GetDirectoryName(path)), fileName = Path.GetFileName(path);
            DeleteWithRetries(Directory.EnumerateFiles(dirPath, fileName + ".incomplete.part*")
                      .Concat(Directory.EnumerateFiles(dirPath, fileName + ".complete.part*")));
        }

        public static void SetDefaultMime(string path, string value)
        {
            SetFileValue(path, "mime", value);
        }
    }

    public static partial class FFmpeg
    {
        private static readonly string Root, Ffprobe;

        private static Process CreateProcess(string path, string arguments)
        {
            var result = new Process
            {
                StartInfo = new ProcessStartInfo(path, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root }
            };
            result.Start();
            return result;
        }

        public static string Analyze(string path)
        {
            try
            {
                var process = CreateProcess(Ffprobe, '"' + path + '"');
                while (!process.StandardError.ReadLine().StartsWith("Input", StringComparison.Ordinal))
                {
                }
                return process.StandardError.ReadToEnd();
            }
            catch
            {
                return "分析失败。";
            }
        }

        public static TimeSpan Parse(string value, TimeSpan defaultValue = default(TimeSpan))
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            return value.Contains(":") ? TimeSpan.Parse(value) : TimeSpan.FromSeconds(double.Parse(value));
        }
    }

    public static class Rbase64
    {
        public static string Encode(string value)
        {
            return LinkConverter.Base64Encode(LinkConverter.Reverse(value), Encoding.UTF8);
        }

        public static string Decode(string value)
        {
            return LinkConverter.Reverse(LinkConverter.Base64Decode(value, Encoding.UTF8));
        }
    }
}