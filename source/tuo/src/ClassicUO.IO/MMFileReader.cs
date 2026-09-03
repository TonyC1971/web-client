using System;
using System.IO;
#if !BROWSER_WASM
using System.IO.MemoryMappedFiles;
#endif
using System.Runtime.CompilerServices;

namespace ClassicUO.IO
{
    public class MMFileReader : FileReader
    {
#if BROWSER_WASM
        // v0.7.9: On WASM, MemoryMappedFile + AcquirePointer don't work
        // — the unmanaged pointer doesn't map to valid wasm linear memory
        // for all file offsets, and the .NET MMF impl tries to malloc the
        // whole file (~629 MiB total across all UO MULs) into managed heap
        // before AcquirePointer returns, hanging the boot indefinitely on
        // memento's 248 MiB anim5.mul. Use the FileStream directly — it
        // already reads from Emscripten MEMFS without copying.
        //
        // v0.7.9 iter 56 ROOT-FIX: this branch USED to override ReadAt<T>
        // with `stackalloc byte[sizeof(T)] + ReadExactly +
        // Unsafe.ReadUnaligned<T>(ref buf[0])`. For T=MapBlock — a struct
        // whose `Cells` field is an `[InlineArray(64)]` of a 3-byte
        // MapCells — that path returned GARBAGE under .NET 10 Mercury MT
        // AOT: the first chunk's tile coordinates came out as wildly
        // wrong values that VARIED every run from the SAME MapAddress
        // input (50072/0, 51200/32848, 13728/4096, 0/26624 for a chunk
        // whose correct bx/by = 1480/1616). That garbage MapBlock then
        // tripped a native "memory access out of bounds" /
        // "WebAssembly.Exception" downstream in AddGameObject. The
        // bisect (iter 45-55) chased it through EnterWorld → SetInWorldTile
        // → Chunk.Load → the tile loop before pinning it here.
        //
        // CUO never had this bug because its WASM MMFileReader does NOT
        // override ReadAt — it inherits the base FileReader.Read<T>()
        // which reads directly into the struct via
        // `new Span<byte>(&v, sizeof(T))`. Reading bytes straight into
        // the struct's own stack storage is AOT-safe for InlineArray
        // structs; `Unsafe.ReadUnaligned` of them is not. Mirror CUO
        // exactly: just wrap the stream in a BinaryReader and let the
        // base class handle Read<T> / ReadAt<T> / ReadAt(Span<byte>).
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
        private unsafe byte* _ptr;

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
                    _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
                    _file = new BinaryReader(new UnmanagedMemoryStream(_ptr, Length));
                }
            }
            catch (Exception ex)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();

                throw new InvalidOperationException("Failed to acquire memory-mapped file pointer.", ex);
            }
        }

        public override BinaryReader Reader => _file;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override unsafe T ReadAt<T>(long offset) => Unsafe.ReadUnaligned<T>(_ptr + offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override unsafe void ReadAt(long offset, Span<byte> buffer) => new ReadOnlySpan<byte>(_ptr + offset, buffer.Length).CopyTo(buffer);

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
