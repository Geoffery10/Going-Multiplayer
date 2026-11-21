using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace GoingMultiplayer
{
    // The guest plays the host's save for the session. It is written to a
    // dedicated slot so the guest's own slot 0/1 are never touched, the previous
    // contents of that slot are backed up, and all game saves are blocked while
    // the session save is mounted (autosaving must never overwrite a real save).
    internal static class SessionSave
    {
        internal const int Slot = 2;
        private const string BackupSuffix = ".coopbak";

        internal static bool Active { get; private set; }
        private static int previousSlot = -1;
        internal static int InstalledBytes { get; private set; }
        internal static string InstalledHash { get; private set; }

        private static string SlotPath
        {
            get { return Path.Combine(Application.persistentDataPath, SaveLoad.GetSavePath(Slot)); }
        }

        private static string BackupPath { get { return SlotPath + BackupSuffix; } }

        internal static void Install(byte[] data, Action<string> report)
        {
            if (data == null || data.Length < 32)
            {
                report("GMP_SAVE_INSTALL FAIL empty");
                return;
            }
            try
            {
                previousSlot = SaveLoad.CURRENT_SAVE_INDEX;
                string slot = SlotPath;
                string backup = BackupPath;
                if (File.Exists(backup)) File.Delete(backup);
                if (File.Exists(slot)) File.Copy(slot, backup, true);
                File.WriteAllBytes(slot, data);
                // Block writes before mounting: MountSave can itself save when
                // the slot is empty.
                Active = true;
                SaveLoad.UnCacheSaveData();
                SaveLoad.MountSave(Slot);
                // Keep the write gate on through the mount above.
                InstalledBytes = data.Length;
                InstalledHash = Hash(data);
                report("GMP_SAVE_INSTALL ok slot=" + Slot + " bytes=" + data.Length
                    + " hash=" + InstalledHash
                    + " data=" + (SaveData.instance != null));
            }
            catch (Exception exception)
            {
                Active = false;
                report("GMP_SAVE_INSTALL FAIL " + exception.GetType().Name
                    + " " + exception.Message);
            }
        }

        internal static void Restore(Action<string> report)
        {
            if (!Active) return;
            try
            {
                // Remount the player's own slot before releasing the write gate.
                if (previousSlot >= 0 && previousSlot != Slot)
                {
                    SaveLoad.UnCacheSaveData();
                    SaveLoad.MountSave(previousSlot);
                }
                string slot = SlotPath;
                string backup = BackupPath;
                if (File.Exists(backup))
                {
                    File.Copy(backup, slot, true);
                    File.Delete(backup);
                    report("GMP_SAVE_RESTORE restored");
                }
                else
                {
                    if (File.Exists(slot)) File.Delete(slot);
                    report("GMP_SAVE_RESTORE cleared");
                }
                SaveLoad.UnCacheSaveData();
                Active = false;
            }
            catch (Exception exception)
            {
                Active = false;
                report("GMP_SAVE_RESTORE FAIL " + exception.GetType().Name);
            }
        }

        internal static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data))
                    .Replace("-", "").ToLowerInvariant();
        }

        // Called once at plugin load. A backup left behind means the previous
        // session ended without a clean leave; keep it and say so.
        internal static void CheckLeftovers(Action<string> report)
        {
            try
            {
                if (File.Exists(BackupPath))
                    report("GMP_SAVE_BACKUP pending=" + BackupPath);
            }
            catch (Exception) { }
        }
    }
}
