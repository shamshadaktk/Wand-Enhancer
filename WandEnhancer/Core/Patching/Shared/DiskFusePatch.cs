using System;
using System.IO;

namespace WandEnhancer.Core.Patching.Shared
{
    /// <summary>
    /// Flips the ASAR-integrity state byte on disk and backs up the original to preserve Authenticode restores.
    /// </summary>
    internal static class DiskFusePatch
    {
        private const string FuseBackupSuffix = ".fusebak";

        public static bool Apply(string exePath, Action<string, ELogType> log)
        {
            long offset = ElectronFuseWire.FindStateFileOffset(exePath);
            if (offset < 0)
            {
                log?.Invoke($"[ENHANCER] No Electron fuse block in {Path.GetFileName(exePath)}; nothing to flip.", ELogType.Warn);
                return false;
            }

            using (var stream = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                stream.Position = offset;
                int original = stream.ReadByte();
                if (original == ElectronFuseWire.StateRemoved)
                {
                    return true;
                }

                string backup = exePath + FuseBackupSuffix;
                if (!File.Exists(backup) || !BackupMatches(backup, (byte)original))
                {
                    // Re-baseline: a leftover backup from an older Wand would restore a stale byte.
                    File.WriteAllBytes(backup, new[] { (byte)original });
                }

                stream.Position = offset;
                stream.WriteByte(ElectronFuseWire.StateRemoved);
            }

            log?.Invoke($"[ENHANCER] ASAR integrity fuse cleared on disk in {Path.GetFileName(exePath)}.", ELogType.Info);
            return true;
        }

        public static void Remove(string exePath, Action<string, ELogType> log)
        {
            string backup = exePath + FuseBackupSuffix;
            if (!File.Exists(backup))
            {
                return;
            }

            long offset = ElectronFuseWire.FindStateFileOffset(exePath);
            if (offset < 0)
            {
                // No fuse block to restore; keep backup.
                log?.Invoke($"[ENHANCER] Could not locate the fuse to restore in {Path.GetFileName(exePath)}; keeping {FuseBackupSuffix}.", ELogType.Warn);
                return;
            }

            byte[] saved = File.ReadAllBytes(backup);
            if (saved.Length == 0)
            {
                log?.Invoke($"[ENHANCER] Fuse backup {Path.GetFileName(backup)} is empty; cannot restore.", ELogType.Warn);
                return;
            }

            using (var stream = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                stream.Position = offset;
                stream.WriteByte(saved[0]);
            }

            log?.Invoke($"[ENHANCER] ASAR integrity fuse restored on disk in {Path.GetFileName(exePath)}.", ELogType.Info);
            File.Delete(backup);
        }

        private static bool BackupMatches(string backup, byte original)
        {
            try
            {
                byte[] saved = File.ReadAllBytes(backup);
                return saved.Length > 0 && saved[0] == original;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
