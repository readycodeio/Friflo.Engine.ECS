// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition.

using System;
using System.Runtime.InteropServices;

// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

/// <summary>
/// Blittable struct passed from CoreCLR to the AOT relay when registering a mod component heap.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct AOTHeapPointers
{
    /// <summary>GCHandle (Normal) of the TypedComponentHeap&lt;T&gt; instance.</summary>
    public IntPtr Self { get; init; }

    /// <summary>Unsafe.SizeOf&lt;T&gt;() - the stride the mod side reports for this component.</summary>
    public int Stride { get; init; }

    public IntPtr GetLength { get; init; } // () -> int
    public IntPtr Resize { get; init; } // (int newCapacity, int copyCount) -> void
    public IntPtr Move { get; init; } // (int from, int to) -> void
    public IntPtr CopyTo { get; init; } // (int srcPos, IntPtr targetSelf, int dstPos) -> void
    public IntPtr SetDefault { get; init; } // (int index) -> void
    public IntPtr SetRangeDefault { get; init; } // (int start, int count) -> void
}

public delegate int    HeapGetCountDelegate();
public delegate void   HeapResizeDelegate   (int newCapacity, int copyCount);
public delegate void   HeapMoveDelegate     (int from, int to);
public delegate void   HeapCopyToDelegate   (int srcPos, IntPtr targetSelf, int dstPos);
public delegate void   HeapSetDefaultDelegate  (int index);
public delegate void   HeapClearRangeDelegate  (int start, int count);
public delegate void   HeapStashDelegate       (int index);