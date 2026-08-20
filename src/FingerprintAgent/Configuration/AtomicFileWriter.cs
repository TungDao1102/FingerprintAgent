using System;
using System.IO;
using System.Text;

namespace FingerprintAgent.Configuration
{
    /// <summary>
    /// Atomic file write helper (CR-02 mitigation).
    ///
    /// On NTFS, renaming a file within the same directory is an atomic operation at the
    /// filesystem level. The pattern is:
    ///   1. Write new content to &lt;path&gt;.tmp in the SAME directory as the target file
    ///      (so the rename stays within one volume/journal).
    ///   2. If the target exists, File.Replace(temp, target, null) — preserves ACLs and
    ///      attributes on the original file (vs File.Move which can drop ACL inheritance).
    ///   3. If the target does not exist (first-write), File.Move(temp, target).
    ///
    /// The temp file is always cleaned up on failure (best-effort). If the process is
    /// killed mid-write, the .tmp file may remain on disk — operators can safely delete it.
    /// The target file is never partially written, so a crash mid-write leaves the
    /// previous valid version intact.
    ///
    /// Shared between the agent process (ConfigLoader, UpdateCheckService) and the
    /// installer CustomAction (SeedProgramDataConfigCore). Source is linked into the
    /// Installer CA DLL via the same pattern as ConfigMerger.cs.
    /// </summary>
    public static class AtomicFileWriter
    {
        /// <summary>
        /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically.
        /// </summary>
        /// <param name="path">Target file path. Must be absolute.</param>
        /// <param name="contents">UTF-8 text to write.</param>
        /// <exception cref="ArgumentNullException">path or contents is null.</exception>
        /// <exception cref="ArgumentException">path is empty or whitespace.</exception>
        /// <exception cref="IOException">I/O failure during write or rename.</exception>
        public static void WriteAllText(string path, string contents)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("path must not be empty or whitespace", nameof(path));
            }
            if (contents == null) throw new ArgumentNullException(nameof(contents));

            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException(
                    "path must include a directory component (File.Replace/Move require same-volume rename)",
                    nameof(path));
            }

            // Suffix must be unique per call so two simultaneous writes to the same target
            // don't collide on .tmp. Use a Guid instead of a single .tmp filename.
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                // File.WriteAllText in .NET Framework 4.8 opens with FileShare.Read
                // (NOT FileShare.None) and closes the handle before we rename. The temp
                // file is fully flushed and closed before the swap. FileShare.Read is
                // safe — we hold the only write handle at the moment of the rename.
                File.WriteAllText(tempPath, contents, new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    // File.Replace preserves ACLs/attributes on the original file across
                    // the swap. Backup file argument (third) is null = no .bak created.
                    File.Replace(tempPath, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            catch
            {
                // Best-effort cleanup of the temp file on any failure. If the rename
                // succeeded, the temp file no longer exists and this is a no-op.
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Swallow cleanup errors — original exception is more important.
                }
                throw;
            }
        }
    }
}
