// Copyright (c) Ullrich Praetz - https://github.com/friflo. All rights reserved.
// See LICENSE file in the project root for full license information.

using System;
#if NET6_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#endif
using Friflo.Engine.ECS.Index;
using Friflo.Json.Burst;
using Friflo.Json.Fliox;
using Friflo.Json.Fliox.Mapper;
using Friflo.Json.Fliox.Mapper.Map;

// ReSharper disable UseNullPropagation
// ReSharper disable StaticMemberInGenericType
// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

/// <remarks>
/// <b>Note:</b> Should not contain any other fields. Reasons:<br/>
/// - to enable maximum efficiency when GC iterate <see cref="Archetype.structHeaps"/> <see cref="Archetype.heapMap"/>
///   for collection.
/// </remarks>
internal sealed class StructHeap<T> : StructHeap, IComponentStash<T>
    where T : struct
{
    /// <summary>
    /// Method only available for debugging. Reasons:<br/>
    /// - it boxes struct values to return them as objects<br/>
    /// - it allows only reading struct values
    /// </summary>
    public override object      GetStashDebug() => componentStash;
    public          ref T       GetStashRef()     => ref componentStash;
    
    // Note: Should not contain any other field. See class <remarks>
    // --- internal fields
    internal            T[]                 components;     //  8
    internal            T                   componentStash; //  sizeof(T)

#if NET6_0_OR_GREATER
    /// <summary>
    /// Whether T can live in the pinned object heap. A component holding managed references cannot,
    /// and handing a raw pointer to one out is unsound anyway, so those keep the unpinned path.
    /// </summary>
    private static readonly bool CanPin = !RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    /// <summary>
    /// Set once <see cref="components"/> lives in the pinned object heap. Only a bool, so it adds
    /// nothing for the GC to trace, which is what the note above is protecting.
    /// </summary>
    private bool pinned;

    private T[] AllocateComponents(int capacity)
        => pinned ? GC.AllocateArray<T>(capacity, pinned: true) : new T[capacity];

    /// <summary>
    /// Moves the component array to the pinned object heap, where it will never be relocated.
    /// Called on the first pointer request, so only components something actually takes a pointer
    /// to pay for it. Every later resize then allocates pinned as well.
    /// </summary>
    private void PinComponents()
    {
        if (pinned || !CanPin)
        {
            return;
        }

        pinned = true;
        var pinnedComponents = GC.AllocateArray<T>(components.Length, pinned: true);
        new ReadOnlySpan<T>(components).CopyTo(pinnedComponents);
        components = pinnedComponents;
    }
#else
    private T[] AllocateComponents(int capacity) => new T[capacity];
#endif

    /// <remarks>
    /// The returned pointer outlives this call, so the array it points into must not move. On
    /// net6.0 and up the array is placed in the pinned object heap on first use, which guarantees
    /// that. The fallback below pins only for the duration of the <c>fixed</c> block, so its result
    /// is valid only while nothing can collect.
    /// </remarks>
    public override IntPtr GetComponentPointer(int index)
    {
        unsafe
        {
#if NET6_0_OR_GREATER
            PinComponents();
            if (pinned)
            {
                return (IntPtr)Unsafe.AsPointer(
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(components), index));
            }
#endif
            fixed (T* ptr = components)
            {
                return (IntPtr)(ptr + index);
            }
        }
    }

    internal StructHeap(int structIndex)
        : base (structIndex)
    {
        components          = new T[ArchetypeUtils.MinCapacity];
    }
    
    internal override void StashComponent(int compIndex) {
        componentStash = components[compIndex];
    }
    
    internal override  void SetBatchComponent(BatchComponent[] components, int compIndex)
    {
        this.components[compIndex] = ((BatchComponent<T>)components[structIndex]).value;
    }
    
    // --- StructHeap
    protected override  int     ComponentsLength    => components.Length;

    internal  override  Type    StructType          => typeof(T);
    
    internal override void ResizeComponents    (int capacity, int count) {
        var newComponents   = AllocateComponents(capacity);
        var curComponents   = components;
        var source          = new ReadOnlySpan<T>(curComponents, 0, count);
        var target          = new Span<T>(newComponents);
        source.CopyTo(target);
        components = newComponents;
    }
    
    internal override void MoveComponent(int from, int to)
    {
        components[to] = components[from];
    }
    
    internal override void CopyComponentTo(int sourcePos, StructHeap target, int targetPos)
    {
        var targetHeap = (StructHeap<T>)target;
        targetHeap.components[targetPos] = components[sourcePos];
    }
    
    internal override void CopyComponent(int sourcePos, StructHeap targetHeap, int targetPos, in CopyContext context, long updateIndexTypes)
    {
        if (typeof(T) == typeof(TreeNode)) {
            return;
        }
        var copyValue       = CopyValueUtils<T>.CopyValue;
        ref T source        = ref components[sourcePos];
        var typedTargetHeap = (StructHeap<T>)targetHeap;
        ref T target        = ref typedTargetHeap.components[targetPos];
        if (StructInfo<T>.HasIndex) {
            AddOrUpdateIndex(source, target, context.target, typedTargetHeap, updateIndexTypes);
        }
        if (copyValue == null) {
            target = source;
        } else {
            copyValue(source, ref target, context);
        }
    }
    
    private static void AddOrUpdateIndex(in T source, in T target, in Entity targetEntity, StructHeap<T> targetHeap, long updateIndexTypes)
    {
        if (((1 << StructInfo<T>.Index) & updateIndexTypes) == 0) {
            StoreIndex.AddIndex(targetEntity.store, targetEntity.Id, source);
        } else {
            targetHeap.componentStash = target;
            StoreIndex.UpdateIndex(targetEntity.store, targetEntity.Id, source, targetHeap);
        }
    }
    
    internal  override  void SetComponentDefault (int compIndex) {
        components[compIndex] = default;
    }
    
    internal  override  void SetComponentsDefault (int compIndexStart, int count)
    {
        var componentSpan = new Span<T>(components, compIndexStart, count);
        componentSpan.Clear();
    }
  
    /// <summary>
    /// Method only available for debugging. Reasons:<br/>
    /// - it boxes struct values to return them as objects<br/>
    /// - it allows only reading struct values
    /// </summary>
    internal override object GetComponentDebug(int compIndex) => components[compIndex];
    
    
    internal override Bytes Write(ObjectWriter writer, int compIndex) {
        var mapper = (TypeMapper<T>)writer.TypeCache.GetTypeMapper(typeof(T));
        ref var value = ref components[compIndex];
        return writer.WriteAsBytesMapper(value, mapper);
    }
    
    internal override void Read(ObjectReader reader, int compIndex, JsonValue json) {
        var mapper = (TypeMapper<T>)reader.TypeCache.GetTypeMapper(typeof(T));
        components[compIndex] = reader.ReadMapper(mapper, json);  // todo avoid boxing within typeMapper, T is struct
    }
    
    internal override  void UpdateIndex (Entity entity) {
        StoreIndex.UpdateIndex(entity.store, entity.Id, components[entity.compIndex], this);
    }
    
    internal override  void AddIndex (Entity entity) {
        StoreIndex.AddIndex(entity.store, entity.Id, components[entity.compIndex]);
    }
    
    internal override  void RemoveIndex (Entity entity) {
        StoreIndex.RemoveIndex(entity.store, entity.Id, this);
    }
    
    internal  override  bool GetComponentMember<TField> (int compIndex, MemberPath memberPath, out TField value, out Exception exception) {
        var getter = (MemberPathGetter<T, TField>)memberPath.getter;
        try {
            exception = null;
            value = getter(components[compIndex]);
            return true;
        }
        catch (Exception e) {
            exception = e;
            value = default;
            return false;
        }
    }
    
    internal  override  bool SetComponentMember<TField>(Entity entity, MemberPath memberPath, TField value, Delegate onMemberChanged, out Exception exception)
    {
        var setter          = (MemberPathSetter<T, TField>)memberPath.setter;
        ref var component   = ref components[entity.compIndex];
        var oldValue        = component;
        try {
            exception = null;
            setter(ref component, value);
            if (onMemberChanged != null) {
                ((OnMemberChanged<T>)onMemberChanged)(ref component, entity, memberPath.path, oldValue);
            }
            return true;
        }
        catch (Exception e) {
            exception = e;
            return false;
        }
    }
}
