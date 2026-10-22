using System;
using System.IO;
using System.Reflection;
using WandEnhancer.Models;

namespace WandEnhancer.Core.Patching.Shared
{
    /// <summary>Deploys the launcher over the Squirrel stub and restores the original.</summary>
    internal static class LauncherDeployment
    {
        private const string StubBackupSuffix = ".stub";
        private const string StubBackupMetaSuffix = ".stub.meta";
        private static readonly string OwnImagePath = Assembly.GetExecutingAssembly().Location;

        public static void Deploy(WeModConfig install, Action<string, ELogType> log)
        {
            string stubPath = Path.Combine(SquirrelRootOf(install), install.ExecutableName);
            string stubBackup = stubPath + StubBackupSuffix;

            // Skip overwriting self during auto-patch.
            if (string.Equals(OwnImagePath, stubPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(stubPath) && !File.Exists(stubBackup))
            {
                AsarSharp.Utils.Extensions.CopyOver(stubPath, stubBackup);
                WriteStubBackupMeta(stubPath + StubBackupMetaSuffix, stubBackup);
            }

            AsarSharp.Utils.Extensions.CopyOver(OwnImagePath, stubPath);

            // Shortcuts point at the stub we just replaced, so give it Wand's own icon (cosmetic).
            if (File.Exists(stubBackup) && !IconTransplant.Copy(stubBackup, stubPath))
            {
                log?.Invoke("[ENHANCER] Could not copy Wand's icon to the launcher; using the default.", ELogType.Warn);
            }

            log?.Invoke("[ENHANCER] Launcher deployed to root directory", ELogType.Info);
        }

        public static void Restore(WeModConfig install)
        {
            string stubPath = Path.Combine(SquirrelRootOf(install), install.ExecutableName);

            // Skip restore if running as deployed launcher (file is locked; UI must restore later).
            if (string.Equals(OwnImagePath, stubPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string stubBackup = stubPath + StubBackupSuffix;
            if (File.Exists(stubBackup))
            {
                if (!StubBackupMatches(stubBackup, stubPath + StubBackupMetaSuffix))
                {
                    throw new IOException($"Stub backup {stubBackup} does not match the deployed launcher; refusing to restore a stale backup.");
                }

                AsarSharp.Utils.Extensions.CopyOver(stubBackup, stubPath);
                File.Delete(stubBackup);
                TryDelete(stubPath + StubBackupMetaSuffix);
            }
            else
            {
                TryDelete(stubPath + StubBackupMetaSuffix);
            }
        }

        /// <summary>True when the backup still belongs to this launcher (or predates identity files).</summary>
        private static bool StubBackupMatches(string stubBackup, string metaPath)
        {
            string expected = ReadStubBackupMeta(metaPath);
            if (expected == null)
            {
                return true;
            }

            return string.Equals(expected, DescribeStubBackup(stubBackup), StringComparison.Ordinal);
        }

        private static string DescribeStubBackup(string stubBackup)
        {
            var info = new FileInfo(stubBackup);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : null;
        }

        private static void WriteStubBackupMeta(string metaPath, string stubBackup)
        {
            try
            {
                File.WriteAllText(metaPath, DescribeStubBackup(stubBackup) ?? string.Empty);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string ReadStubBackupMeta(string metaPath)
        {
            try
            {
                return File.Exists(metaPath) ? File.ReadAllText(metaPath) : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public static string SquirrelRootOf(WeModConfig install)
        {
            string root = Directory.GetParent(install.RootDirectory)?.FullName;
            if (string.IsNullOrEmpty(root))
            {
                throw new Exception("[ENHANCER] Cannot determine Squirrel root directory");
            }

            return root;
        }
    }
}
