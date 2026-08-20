using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using FingerprintAgent.Configuration;
using Xunit;

namespace FingerprintAgent.Tests.Configuration
{
    public class AtomicFileWriterTests : IDisposable
    {
        private readonly string _tempDir;
        private bool _disposed;

        public AtomicFileWriterTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    if (Directory.Exists(_tempDir))
                        Directory.Delete(_tempDir, recursive: true);
                }
                catch { }
            }
        }

        // ---------- Helpers ----------

        private string TmpFilesInDir()
        {
            return string.Join(",", Directory.GetFiles(_tempDir, "*.tmp"));
        }

        // ---------- Happy path ----------

        [Fact]
        public void WriteAllText_NewFile_CreatesReadableFileAndNoTempLeak()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "newfile.json");
            Assert.False(File.Exists(path));

            // Act
            AtomicFileWriter.WriteAllText(path, "hello");

            // Assert — file readable with the expected content
            Assert.True(File.Exists(path));
            Assert.Equal("hello", File.ReadAllText(path));

            // Assert — no .tmp residue (Finding 1 property: no .tmp leak on success)
            Assert.Equal("", TmpFilesInDir());
        }

        [Fact]
        public void WriteAllText_ExistingFile_OverwritesAndNoTempLeak()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "existing.json");
            File.WriteAllText(path, "ORIGINAL");

            // Act
            AtomicFileWriter.WriteAllText(path, "REPLACED");

            // Assert
            Assert.Equal("REPLACED", File.ReadAllText(path));
            Assert.Equal("", TmpFilesInDir());
        }

        [Fact]
        public void WriteAllText_EmptyContents_Succeeds()
        {
            var path = Path.Combine(_tempDir, "empty.json");

            AtomicFileWriter.WriteAllText(path, "");

            Assert.True(File.Exists(path));
            Assert.Equal("", File.ReadAllText(path));
            Assert.Equal("", TmpFilesInDir());
        }

        [Fact]
        public void WriteAllText_NullContents_ThrowsArgumentNullException()
        {
            var path = Path.Combine(_tempDir, "x.json");
            Assert.Throws<ArgumentNullException>(() => AtomicFileWriter.WriteAllText(path, null));
            Assert.Equal("", TmpFilesInDir());
        }

        // ---------- Edge cases (input validation) ----------

        [Fact]
        public void WriteAllText_NullPath_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => AtomicFileWriter.WriteAllText(null, "x"));
        }

        [Fact]
        public void WriteAllText_EmptyPath_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => AtomicFileWriter.WriteAllText("", "x"));
        }

        [Fact]
        public void WriteAllText_WhitespacePath_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => AtomicFileWriter.WriteAllText("   ", "x"));
        }

        [Fact]
        public void WriteAllText_PathWithoutDirectory_ThrowsArgumentException()
        {
            // "config.json" has no directory component, so File.Replace/Move would fail.
            Assert.Throws<ArgumentException>(() => AtomicFileWriter.WriteAllText("config.json", "x"));
        }

        // ---------- Mid-write failure semantics ----------

        [Fact]
        public void WriteAllText_TargetFileLocked_LeavesTargetUnchangedAndNoTempLeak()
        {
            // Arrange — pre-existing target so the Replace path is exercised
            var path = Path.Combine(_tempDir, "locked.json");
            File.WriteAllText(path, "ORIGINAL");

            // Lock the target with FileShare.None so File.Replace will fail with IOException
            using (var lockHandle = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Act + Assert — write should throw
                Assert.Throws<IOException>(() => AtomicFileWriter.WriteAllText(path, "NEW"));
            }

            // Assert — target file unchanged (Finding 1 critical property)
            Assert.Equal("ORIGINAL", File.ReadAllText(path));

            // Assert — no .tmp residue (Finding 1 critical property: cleanup on failure)
            Assert.Equal("", TmpFilesInDir());
        }

        [Fact]
        public void WriteAllText_FirstWriteToLockedTargetDirectory_LeavesNoTempLeak()
        {
            // Arrange — no pre-existing target so the Move path is exercised, then
            // make the rename fail by locking the parent dir semantics: easiest way is
            // to point at a path whose directory does not exist and cannot be created.
            // Use a path under a file (not a directory) so the temp WriteAllText fails.
            var blockerFile = Path.Combine(_tempDir, "blocker");
            File.WriteAllText(blockerFile, "i am a file, not a directory");

            // path = <file>/x.json — WriteAllText into a non-existent subdir of a file throws
            var path = Path.Combine(blockerFile, "x.json");

            // Act + Assert
            Assert.ThrowsAny<Exception>(() => AtomicFileWriter.WriteAllText(path, "x"));

            // Assert — no .tmp residue anywhere in _tempDir
            Assert.Equal("", TmpFilesInDir());
        }

        // ---------- Concurrency ----------

        [Fact]
        public void WriteAllText_ConcurrentWritesToSameTarget_CleanupOnFailureNoTempLeak()
        {
            // Arrange
            var path = Path.Combine(_tempDir, "concurrent.json");

            // Act — 10 writers hit the same fresh target concurrently. The atomicity
            // claim here is at the NTFS rename level: no partial write is ever visible.
            // However, the File.Move branch (target does not exist) is NOT serialized
            // across writers — net48 File.Move throws IOException if the target
            // already exists by the time the call lands, so most writers fail. The
            // critical invariant we verify here is that failures clean up their .tmp
            // files (per the catch block in WriteAllText) so no .tmp residue survives.
            int succeeded = 0;
            int failed = 0;
            Parallel.For(0, 10, i =>
            {
                try
                {
                    AtomicFileWriter.WriteAllText(path, $"content-{i}");
                    Interlocked.Increment(ref succeeded);
                }
                catch (IOException)
                {
                    // Race lost — another writer beat this one to the target. The
                    // catch block in AtomicFileWriter should have cleaned up this
                    // writer's .tmp file before re-throwing.
                    Interlocked.Increment(ref failed);
                }
            });

            // Assert — at least one writer succeeded (the file exists with valid content)
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.StartsWith("content-", content);
            int writerIdx = int.Parse(content.Substring("content-".Length));
            Assert.InRange(writerIdx, 0, 9);

            // Assert — failed writers were cleaned up (Finding 1 critical property)
            Assert.Equal("", TmpFilesInDir());
        }

        // ---------- ACL preservation on Replace path ----------

        [Fact]
        public void WriteAllText_ExistingTarget_PreservesCustomAcl()
        {
            // Arrange — create a target with a custom ACL that does NOT match the parent
            // dir default (add an explicit ACE that would be lost on File.Move's
            // inherited-ACL path).
            var path = Path.Combine(_tempDir, "acl.json");
            File.WriteAllText(path, "ORIGINAL");

            var security = new FileSecurity();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow));
            File.SetAccessControl(path, security);

            var originalSddl = File.GetAccessControl(path)
                .GetSecurityDescriptorSddlForm(AccessControlSections.All);

            // Act — File.Replace path (target exists)
            AtomicFileWriter.WriteAllText(path, "REPLACED");

            // Assert — content replaced
            Assert.Equal("REPLACED", File.ReadAllText(path));

            // Assert — ACL preserved exactly (Finding 1 critical property)
            var newSddl = File.GetAccessControl(path)
                .GetSecurityDescriptorSddlForm(AccessControlSections.All);
            Assert.Equal(originalSddl, newSddl);

            // Assert — no .tmp residue
            Assert.Equal("", TmpFilesInDir());
        }
    }
}
