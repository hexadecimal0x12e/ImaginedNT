using System.Runtime.CompilerServices;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static partial class CrtHeapApis
{
    private sealed record AlignedBlock(ulong Base, ulong Size, ulong Alignment, ulong Offset);
    private static readonly ConditionalWeakTable<ImaginedProcess, Dictionary<ulong, AlignedBlock>> AlignedBlocks = new();

    [PartiallyImaginedApi(Module, "_aligned_malloc", ApiCategory.Memory, "ucrtbase!_aligned_malloc",
        "api-ms-win-crt-private-l1-1-0!_o__aligned_malloc", "ucrtbase!_o__aligned_malloc")]
    private static ulong AlignedMalloc(ApiCallContext ctx) => AllocateAligned(ctx, ctx.Arg(0), ctx.Arg(1), 0);

    [PartiallyImaginedApi(Module, "_aligned_offset_malloc", ApiCategory.Memory, "ucrtbase!_aligned_offset_malloc",
        "api-ms-win-crt-private-l1-1-0!_o__aligned_offset_malloc", "ucrtbase!_o__aligned_offset_malloc")]
    private static ulong AlignedOffsetMalloc(ApiCallContext ctx) => AllocateAligned(ctx, ctx.Arg(0), ctx.Arg(1), ctx.Arg(2));

    [PartiallyImaginedApi(Module, "_aligned_realloc", ApiCategory.Memory, "ucrtbase!_aligned_realloc",
        "api-ms-win-crt-private-l1-1-0!_o__aligned_realloc", "ucrtbase!_o__aligned_realloc")]
    private static ulong AlignedRealloc(ApiCallContext ctx) => ResizeAligned(ctx, ctx.Arg(0), ctx.Arg(1), ctx.Arg(2), 0, false);

    [PartiallyImaginedApi(Module, "_aligned_offset_realloc", ApiCategory.Memory, "ucrtbase!_aligned_offset_realloc",
        "api-ms-win-crt-private-l1-1-0!_o__aligned_offset_realloc", "ucrtbase!_o__aligned_offset_realloc")]
    private static ulong AlignedOffsetRealloc(ApiCallContext ctx) => ResizeAligned(ctx, ctx.Arg(0), ctx.Arg(1), ctx.Arg(2), ctx.Arg(3), false);

    [PartiallyImaginedApi(Module, "_aligned_recalloc", ApiCategory.Memory, "ucrtbase!_aligned_recalloc",
        "api-ms-win-crt-private-l1-1-0!_o__aligned_recalloc", "ucrtbase!_o__aligned_recalloc")]
    private static ulong AlignedRecalloc(ApiCallContext ctx) => Product(ctx, ctx.Arg(1), ctx.Arg(2), out var size)
        ? ResizeAligned(ctx, ctx.Arg(0), size, ctx.Arg(3), 0, true) : 0;

    [PartiallyImaginedApi(Module, "_aligned_offset_recalloc", ApiCategory.Memory, "ucrtbase!_aligned_offset_recalloc",
        "api-ms-win-crt-private-l1-1-0!_o__aligned_offset_recalloc", "ucrtbase!_o__aligned_offset_recalloc")]
    private static ulong AlignedOffsetRecalloc(ApiCallContext ctx) => Product(ctx, ctx.Arg(1), ctx.Arg(2), out var size)
        ? ResizeAligned(ctx, ctx.Arg(0), size, ctx.Arg(3), ctx.Arg(4), true) : 0;

    [PartiallyImaginedApi(Module, "_aligned_free", ApiCategory.Memory, "ucrtbase!_aligned_free",
        "api-ms-win-crt-private-l1-1-0!_o__aligned_free", "ucrtbase!_o__aligned_free")]
    private static ulong AlignedFree(ApiCallContext ctx)
    {
        FreeAligned(ctx, ctx.Arg(0));
        return 0;
    }

    [PartiallyImaginedApi(Module, "_aligned_msize", ApiCategory.Memory, "ucrtbase!_aligned_msize",
        "api-ms-win-crt-private-l1-1-0!_o__aligned_msize", "ucrtbase!_o__aligned_msize")]
    private static ulong AlignedSize(ApiCallContext ctx)
    {
        var blocks = AlignedBlocks.GetValue(ctx.Process, static _ => new());
        lock (blocks)
        {
            if (blocks.TryGetValue(ctx.Arg(0), out var block) && block.Alignment == ctx.Arg(1) && block.Offset == ctx.Arg(2))
                return block.Size;
            ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid aligned block or alignment/offset at 0x{ctx.Arg(0):X}", ctx.Process.Id, ctx.Thread.Id);
            CrtLocaleData.SetErrno(22);
            return ulong.MaxValue;
        }
    }

    private static ulong AllocateAligned(ApiCallContext ctx, ulong size, ulong alignment, ulong offset)
    {
        if (alignment == 0 || (alignment & (alignment - 1)) != 0 || (offset != 0 && offset >= size))
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid alignment {alignment} or offset {offset} for size {size}", ctx.Process.Id, ctx.Thread.Id);
            CrtLocaleData.SetErrno(22);
            return 0;
        }
        var mask = Math.Max(alignment, 8UL) - 1;
        if (mask > MaxSize - 8 || Math.Max(size, 1UL) > MaxSize - mask - 8)
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}: aligned allocation size overflow", ctx.Process.Id, ctx.Thread.Id);
            CrtLocaleData.SetErrno(12);
            return 0;
        }
        var blocks = AlignedBlocks.GetValue(ctx.Process, static _ => new());
        lock (blocks)
        {
            var address = Allocate(ctx, Math.Max(size, 1UL) + mask + 8);
            if (address == 0) return 0;
            // Keep aligned pointers distinct from the underlying ordinary heap blocks.
            var result = ((address + 8 + offset + mask) & ~mask) - offset;
            try
            {
                blocks.Add(result, new AlignedBlock(address, size, alignment, offset));
            }
            catch (OutOfMemoryException ex)
            {
                ctx.Process.Heap.Free(address, ctx.Thread.Id);
                ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot track aligned allocation: {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
                CrtLocaleData.SetErrno(12);
                return 0;
            }
            return result;
        }
    }

    private static bool FreeAligned(ApiCallContext ctx, ulong address)
    {
        if (address == 0) return true;
        var blocks = AlignedBlocks.GetValue(ctx.Process, static _ => new());
        lock (blocks)
        {
            if (!blocks.TryGetValue(address, out var block))
            {
                ctx.Runtime.Trace.Warn($"{ctx.Name}: unknown aligned block at 0x{address:X}", ctx.Process.Id, ctx.Thread.Id);
                CrtLocaleData.SetErrno(22);
                return false;
            }
            if (!ctx.Process.Heap.Free(block.Base, ctx.Thread.Id)) return false;
            blocks.Remove(address);
            return true;
        }
    }

    private static ulong ResizeAligned(ApiCallContext ctx, ulong address, ulong size, ulong alignment, ulong offset, bool zero)
    {
        if (address != 0 && size == 0)
        {
            FreeAligned(ctx, address);
            return 0;
        }
        var blocks = AlignedBlocks.GetValue(ctx.Process, static _ => new());
        lock (blocks)
        {
            blocks.TryGetValue(address, out var previous);
            if (address != 0 && (previous is null || previous.Alignment != alignment || previous.Offset != offset))
            {
                ctx.Runtime.Trace.Warn($"{ctx.Name}: unknown aligned block or changed alignment at 0x{address:X}", ctx.Process.Id, ctx.Thread.Id);
                CrtLocaleData.SetErrno(22);
                return 0;
            }
            var result = AllocateAligned(ctx, size, alignment, offset);
            if (result == 0) return 0;
            var copied = Math.Min(previous?.Size ?? 0, size);
            if ((copied != 0 && !CrtMemory.Copy(ctx, result, address, copied)) ||
                (zero && size > copied && !CrtMemory.Fill(ctx, result + copied, size - copied, 0, 1)))
            {
                FreeAligned(ctx, result);
                ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot initialize resized aligned block", ctx.Process.Id, ctx.Thread.Id);
                return 0;
            }
            if (address != 0 && !FreeAligned(ctx, address))
            {
                FreeAligned(ctx, result);
                return 0;
            }
            return result;
        }
    }
}
