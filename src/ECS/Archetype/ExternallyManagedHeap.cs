// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition.

using System;
using System.Runtime.InteropServices;
using Friflo.Json.Burst;
using Friflo.Json.Fliox;
using Friflo.Json.Fliox.Mapper;

// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

/// <summary>
/// AOT-side StructHeap backed by a TypedComponentHeap&lt;T&gt; living in the embedded CoreCLR runtime.
/// All mutations are dispatched through function pointers into CoreCLR so write barriers always
/// fire on the managed side. The AOT process never writes into the component array directly, and
/// never forms an address into it: a mod component may hold managed references, so the array cannot
/// be pinned, and only the runtime that owns it can safely turn a slot into a reference. Callers
/// name a component by this heap's <see cref="Self"/> handle plus an index instead.
/// </summary>
public sealed class ExternallyManagedHeap : StructHeap
{
    // Opaque GCHandle of the CoreCLR TypedComponentHeap<T>. Passed as context into CopyTo.
    private readonly IntPtr self;

    // Delegates wrapping the CoreCLR function pointers. Stored as fields so the GC on the AOT
    // side doesn't collect the wrapper objects (the CoreCLR side keeps the underlying stubs alive
    // via PinnedDelegateStore, but the AOT-side delegate wrappers are separate objects).
    private readonly HeapGetCountDelegate   getLength;
    private readonly HeapResizeDelegate     resize;
    private readonly HeapMoveDelegate       move;
    private readonly HeapCopyToDelegate     copyTo;
    private readonly HeapSetDefaultDelegate setDefault;
    private readonly HeapClearRangeDelegate setRangeDefault;

    public readonly int Stride;

    /// <summary>
    /// GCHandle of the CoreCLR heap backing this one, for handing to the mod side so it can reach
    /// its own array without an address crossing the runtime boundary.
    /// </summary>
    public IntPtr Self => self;

    internal ExternallyManagedHeap(int structIndex, AOTHeapPointers pointers)
        : base(structIndex)
    {
        Stride = pointers.Stride;
        self = pointers.Self;

        getLength       = Marshal.GetDelegateForFunctionPointer<HeapGetCountDelegate>  (pointers.GetLength);
        resize          = Marshal.GetDelegateForFunctionPointer<HeapResizeDelegate>    (pointers.Resize);
        move            = Marshal.GetDelegateForFunctionPointer<HeapMoveDelegate>      (pointers.Move);
        copyTo          = Marshal.GetDelegateForFunctionPointer<HeapCopyToDelegate>    (pointers.CopyTo);
        setDefault      = Marshal.GetDelegateForFunctionPointer<HeapSetDefaultDelegate>(pointers.SetDefault);
        setRangeDefault = Marshal.GetDelegateForFunctionPointer<HeapClearRangeDelegate>(pointers.SetRangeDefault);
    }

    // -------------------------------------------------------------------------
    // ReadyM extension
    // -------------------------------------------------------------------------

    /// <summary>Always throws. Use <see cref="Self"/> and an index instead.</summary>
    public override IntPtr GetComponentPointer(int index)
        => throw new NotSupportedException(
            "A mod component has no address the AOT side may hold: its array lives in the embedded " +
            "runtime and cannot be pinned. Pass Self and the index to the mod instead.");

    // -------------------------------------------------------------------------
    // StructHeap core
    // -------------------------------------------------------------------------

    protected override int ComponentsLength => getLength();

    internal override void ResizeComponents(int capacity, int count) => resize(capacity, count);

    internal override void MoveComponent(int from, int to) => move(from, to);

    internal override void CopyComponentTo(int sourcePos, StructHeap targetHeap, int targetPos)
    {
        var target = (ExternallyManagedHeap)targetHeap;
        copyTo(sourcePos, target.self, targetPos);
    }

    internal override void CopyComponent(
        int sourcePos, StructHeap targetHeap, int targetPos,
        in CopyContext context, long updateIndexTypes)
    {
        // Mod components never carry ECS indices - same path as CopyComponentTo.
        var target = (ExternallyManagedHeap)targetHeap;
        copyTo(sourcePos, target.self, targetPos);
    }

    internal override void SetComponentDefault(int compIndex) => setDefault(compIndex);

    internal override void SetComponentsDefault(int compIndexStart, int count)
        => setRangeDefault(compIndexStart, count);

    // -------------------------------------------------------------------------
    // Index support - mod components never participate in ECS indexes
    // -------------------------------------------------------------------------

    internal override void StashComponent(int compIndex) 
        => throw new NotSupportedException("Stash is not supported for mod components.");

    internal override void UpdateIndex(Entity entity) { }
    internal override void AddIndex(Entity entity) { }
    internal override void RemoveIndex(Entity entity) { }

    // -------------------------------------------------------------------------
    // Batch operations - not supported via AOT dispatch path
    // -------------------------------------------------------------------------

    internal override void SetBatchComponent(BatchComponent[] batchComponents, int compIndex)
    {
        // var comp = (ModBatchComponent)batchComponents[structIndex];
        // actually, this will never happen?
    }

    // -------------------------------------------------------------------------
    // Debug / serialization
    // -------------------------------------------------------------------------

    internal override Type StructType => typeof(ModComponentMarker);

    public override object GetStashDebug()
        => "(mod component stash - opaque, inspect via CoreCLR side)";

    internal override object GetComponentDebug(int compIndex)
        => $"(mod component [{compIndex}] - opaque, inspect via CoreCLR side)";

    internal override Bytes Write(ObjectWriter writer, int compIndex)
        => throw new NotSupportedException("JSON serialization is not supported for plugin components.");

    internal override void Read(ObjectReader reader, int compIndex, JsonValue json)
        => throw new NotSupportedException("JSON deserialization is not supported for plugin components.");

    // -------------------------------------------------------------------------
    // Member access - not supported
    // -------------------------------------------------------------------------

    internal override bool GetComponentMember<TField>(
        int compIndex, MemberPath memberPath, out TField value, out Exception exception)
    {
        exception = new NotSupportedException("Member access is not supported for plugin components.");
        value = default;
        return false;
    }

    internal override bool SetComponentMember<TField>(
        Entity entity, MemberPath memberPath, TField value,
        Delegate onMemberChanged, out Exception exception)
    {
        exception = new NotSupportedException("Member access is not supported for plugin components.");
        return false;
    }
}

/// <summary>
/// Marker type returned by <see cref="ExternallyManagedHeap.StructType"/> for debugging.
/// Not used as an actual ECS component.
/// </summary>
internal class ModComponentMarker;