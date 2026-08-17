// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition - partial extension to NativeAOT.

// ReSharper disable once CheckNamespace

namespace Friflo.Engine.ECS;

public sealed partial class NativeAOT
{
    /// <summary>Whether the process-wide schema has been created. Reading this never creates it.</summary>
    public static bool SchemaCreated => EntitySchemaHolder.IsCreated;

    public int RegisterModComponent(ModComponentRegistration pointers)
    {
        var structIndex = schemaTypes.components.Count + 1;
        schemaTypes.components.Add(new ModComponentType(structIndex, pointers));

        return structIndex;
    }
}