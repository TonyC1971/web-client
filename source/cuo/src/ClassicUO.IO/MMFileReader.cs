using System;
using System.IO;
#if !BROWSER_WASM
using System.IO.MemoryMappedFiles;
#endif

namespace ClassicUO.IO
{
    public class MMFileReader : FileReader
    {
#if BROWSER_WASM
        // On WASM, MemoryMappedFile + AcquirePointer don't work:
        // the unmanaged pointer doesn't map to valid wasm linear
        // memory for all file offsets. Use the FileStream directly
        // — it already reads from Emscripten MEMFS. No copy needed,
        // saving ~629 MiB of managed heap that was causing OOM
        // crashes at tick 100-500.
        private readonly BinaryReader _file;

        public MMFileReader(FileStream stream) : base(stream)
        {
            if (Length <= 0)
                return;

            _file = new BinaryReader(stream);
        }

        public override BinaryReader Reader => _file;

        public override void Dispose()
        {
            base.Dispose();
        }
#else
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly MemoryMappedFile _mmf;
        private readonly BinaryReader _file;

        public MMFileReader(FileStream stream) : base(stream)
        {
            if (Length <= 0)
                return;

            _mmf = MemoryMappedFile.CreateFromFile
            (
                stream,
                null,
                0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                false
            );

            _accessor = _mmf.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);

            try
            {
                unsafe
                {
                    byte* ptr = null;
                    _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    _file = new BinaryReader(new UnmanagedMemoryStream(ptr, Length));
                }
            }
            catch (Exception ex)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();

                throw new InvalidOperationException("Failed to acquire memory-mapped file pointer.", ex);
            }
        }

        public override BinaryReader Reader => _file;

        public override void Dispose()
        {
            _accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor?.Dispose();
            _mmf?.Dispose();

            base.Dispose();
        }
#endif
    }
}
