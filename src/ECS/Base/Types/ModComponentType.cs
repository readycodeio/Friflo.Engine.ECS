// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition.

[StructLayout(LayoutKind.Sequential)]
public struct ModComponentInfo
{
    /// <summary><c>Unsafe.SizeOf&lt;T&gt;()</c> - informs the AOT side of component stride.</summary>
    public int Stride;

    /// <summary>1 if T is blittable and raw pointer iteration is available, 0 otherwise.</summary>
    public byte IsBlittable;
    
    public IntPtr AllocHeap;
    public IntPtr WriteSnapshot;
    public IntPtr ReadSnapshot;
    public IntPtr WriteDelta;
    public IntPtr ReadDelta;

    /// <summary>Function pointer: 1 if the component was changed from the API (server override).</summary>
    public IntPtr ChangedFromApi;
}

/// <summary>
/// Delegate matching the AllocHeap function pointer in ModComponentInfo.
/// </summary>
public delegate void AllocHeapDelegate(int capacity, IntPtr outAOTHeapPointers);


internal sealed class ModComponentType : ComponentType
{
    public override string ToString() => $"ModComponent[{StructIndex}] {ComponentKey} stride:{info.Stride}";

    private readonly ModComponentInfo info;

    // Wrapped and stored as a field so the AOT-side GC doesn't collect the delegate wrapper
    // between archetype materializations.
    private readonly AllocHeapDelegate allocHeap;

    /// <param name="componentName">
    /// The mod component's full type name. It is the schema's key for this component, which is how a caller
    /// that did not create the schema finds its struct index: keying by the struct index, as this used to,
    /// makes the key the answer to the only question worth asking it.
    /// </param>
    internal ModComponentType(int structIndex, ModComponentInfo info, string componentName)
        : base(
            componentKey:   componentName,
            structIndex:    structIndex,
            type:           typeof(ModComponentMarker),
            indexType:      null,
            indexValueType: null,
            byteSize:       info.Stride,
            relationType:   null,
            keyType:        null)
    {
        if (string.IsNullOrEmpty(componentName)) {
            throw new ArgumentException(
                "A mod component needs a name. It is the schema's only handle on a component that has no " +
                "managed type.", nameof(componentName));
        }
        this.info = info;
        allocHeap    = Marshal.GetDelegateForFunctionPointer<AllocHeapDelegate>(info.AllocHeap);

        Unsafe.AsRef(in IsBlittable) = info.IsBlittable != 0;
    }

    // Called by the ECS each time an archetype that contains this component type is first
    // materialized. Calls back into CoreCLR to allocate a fresh TypedComponentHeap<T>.
    internal override unsafe StructHeap CreateHeap()
    {
        // Stack-allocate the output struct. The call is synchronous, so the stack frame
        // is alive for the duration of AllocHeapImpl writing into it.
        AOTHeapPointers pointers;
        allocHeap(ArchetypeUtils.MinCapacity, (IntPtr)(&pointers));
        return new ExternallyManagedHeap(StructIndex, pointers);
    }

    internal override bool RemoveEntityComponent(Entity entity)
        => throw new NotSupportedException(
            $"Cannot dynamically remove mod component [{StructIndex}] from an entity. " +
            "Mod component membership is fixed at entity creation.");

    internal override bool AddEntityComponent(Entity entity)
        => throw new NotSupportedException(
            $"Cannot dynamically add mod component [{StructIndex}] to an entity. " +
            "Mod component membership is fixed at entity creation.");

    internal override bool AddEntityComponentValue(Entity entity, object value)
        => throw new NotSupportedException(
            "Cannot add mod component by value. " +
            "Mod component membership is fixed at entity creation.");
}