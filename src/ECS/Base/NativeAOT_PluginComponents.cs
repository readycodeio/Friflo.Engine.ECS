// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition - partial extension to NativeAOT.

// ReSharper disable once CheckNamespace

namespace Friflo.Engine.ECS;

public sealed partial class NativeAOT
{
    /// <summary>
    /// Whether the process-wide schema has been created. Reading this never creates it.
    /// <para>
    /// Deliberately NOT public: outside this assembly it only enables `if (!SchemaCreated) Create()`, which
    /// is unsound across threads. Use <see cref="SchemaBootstrap.TryInitializeFromRegisteredTypes"/> or
    /// <see cref="SchemaBootstrap.TryInitializeFromLoadedAssemblies"/> and read the result, or
    /// <see cref="SchemaBootstrap.SchemaSource"/> for diagnostics.
    /// </para>
    /// </summary>
    internal static bool SchemaCreated => EntitySchemaHolder.IsCreated;

    /// <summary>
    /// Registers a component defined by a mod, which has no managed type on this side.
    /// </summary>
    /// <param name="componentName">
    /// The component's full type name. The sealed schema keys the component by it, so a caller that did not
    /// create the schema can still resolve the struct index it actually got rather than assuming the one this
    /// call returned.
    /// </param>
    public int RegisterModComponent(ModComponentInfo pointers, string componentName)
    {
        var structIndex = schemaTypes.components.Count + 1;
        schemaTypes.components.Add(new ModComponentType(structIndex, pointers, componentName));

        return structIndex;
    }
}