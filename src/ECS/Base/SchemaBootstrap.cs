// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition.

using System;

// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

/// <summary>
/// How the process-wide <see cref="EntitySchema"/> was created. Exactly one mechanism creates it: they are
/// mutually exclusive, not combinable.
/// </summary>
public enum EntitySchemaSource
{
    /// <summary>No schema has been created yet. Creating an <see cref="EntityStore"/> in this state throws.</summary>
    NotCreated,

    /// <summary>Created by <see cref="SchemaBootstrap.TryInitializeFromRegisteredTypes"/> from types
    /// registered on a <see cref="NativeAOT"/> instance.</summary>
    RegisteredTypes,

    /// <summary>Created by <see cref="SchemaBootstrap.TryInitializeFromLoadedAssemblies"/> by scanning the
    /// assemblies that were loaded at the time.</summary>
    LoadedAssemblies,
}

/// <summary>
/// The entry point for creating the process-wide <see cref="EntitySchema"/>. Every path goes through here,
/// so there is one place to look for how and when the schema comes into existence.
/// <para>
/// There are two mechanisms, both explicit, differing only in how component types are discovered:
/// <list type="bullet">
///   <item><see cref="TryInitializeFromRegisteredTypes"/> - every type registered by hand on a
///   <see cref="NativeAOT"/> instance. Required when component types are not .NET types in this runtime,
///   for example server-side mod components that cross a runtime boundary as a stride plus function
///   pointers.</item>
///   <item><see cref="TryInitializeFromLoadedAssemblies"/> - component, tag and script types discovered by
///   scanning the assemblies currently loaded. Only sees what is loaded when it runs, so every mod
///   assembly must already be loaded.</item>
/// </list>
/// Exactly one of them creates the schema. Mixing them throws: a reflection scan cannot see types
/// registered explicitly, nor the other way round, so there is no meaningful way to combine them.
/// </para>
/// <para>
/// Initializing twice is a hard failure by default. A process that legitimately builds many containers over
/// one schema can opt out with <see cref="AllowRepeatedInitializationForTests"/>, and even then the repeat
/// must produce a matching schema shape. Callers do not check first, and deliberately cannot: see the
/// synchronization contract in <c>EntitySchemaHolder</c> for why no "has it been created" flag is published.
/// </para>
/// <para>
/// Whichever you use, call it after all mods are loaded and before the first <see cref="EntityStore"/>
/// exists. That ordering is the caller's responsibility and no amount of locking here can supply it.
/// </para>
/// </summary>
public static class SchemaBootstrap
{
    /// <summary>
    /// How the schema was created, or <see cref="EntitySchemaSource.NotCreated"/> if it has not been.
    /// For diagnostics and assertions. Do NOT branch on this to decide whether to initialize: call
    /// <c>TryInitialize...</c> unconditionally and read its result instead.
    /// </summary>
    public static EntitySchemaSource SchemaSource => EntitySchemaHolder.Source;

    /// <summary>
    /// Whether <see cref="AllowRepeatedInitializationForTests"/> is in effect for this process. Whether a
    /// repeated initialization throws depends on it, so a test asserting either behaviour should state which
    /// mode it expects rather than assume the process it happens to run in.
    /// </summary>
    public static bool RepeatedInitializationAllowed => EntitySchemaHolder.RepeatsAllowed;

    /// <summary>
    /// Downgrades a repeated initialization through the same mechanism from a hard failure to a report on
    /// <paramref name="onRepeat"/>. FOR TEST PROCESSES ONLY.
    /// <para>
    /// A test process legitimately builds many containers over one schema: the schema is process-global and
    /// immutable, so every container after the first has nothing to create. Production has one container and
    /// must keep the strict behaviour, because a second initialization there means something is registering
    /// component types that are not in the sealed schema.
    /// </para>
    /// <para>
    /// This does not make repeats unconditionally safe, and is not meant to. A repeat whose schema shape
    /// differs from the one in place still fails: that caller's component types are not the ones in the
    /// sealed schema, and letting it continue is exactly how struct indices come to mean different things to
    /// different callers. Enabling this also requires somewhere to report to, so it cannot be turned on
    /// silently.
    /// </para>
    /// </summary>
    public static void AllowRepeatedInitializationForTests(Action<string> onRepeat)
        => EntitySchemaHolder.AllowRepeatedInitialization(onRepeat);

    /// <summary>Restores the default, where a repeated initialization is a hard failure.</summary>
    /// <summary>
    /// Makes the schema usable only inside an explicit window, for a test process. Off by default, so
    /// production is unaffected and the schema is usable from the moment it is created.
    /// <para>
    /// With it on, a schema is created unusable and each test opens a window, uses it, and closes it. A
    /// Friflo schema cannot really be rebuilt, so the window is the honest way to express per-test setup:
    /// touching the schema outside one fails at the mistake rather than letting a test lean on what an
    /// earlier one left behind.
    /// </para>
    /// </summary>
    public static void EnforceUsageWindowForTests() => EntitySchemaHolder.EnforceUsageWindow();

    /// <summary>Makes the schema usable. Fails if a window is already open, or none was ever created.</summary>
    public static void OpenUsageWindow() => EntitySchemaHolder.OpenUsage();

    /// <summary>Makes the schema unusable again. Fails if no window is open.</summary>
    public static void CloseUsageWindow() => EntitySchemaHolder.CloseUsage();

    public static void DisallowRepeatedInitialization()
        => EntitySchemaHolder.DisallowRepeatedInitialization();

    /// <summary>
    /// Creates the schema from the types registered on <paramref name="aot"/>, unless it already exists.
    /// Register every component, tag, script, indexed component and mod component on it first: the schema is
    /// immutable, so anything missing is missing for the lifetime of the process.
    /// </summary>
    /// <exception cref="InvalidOperationException">A schema already exists and this call is not a permitted
    /// repeat: it came from the other mechanism, repeats are not allowed, or the shapes differ.</exception>
    public static EntitySchema InitializeFromRegisteredTypes(NativeAOT aot)
    {
        if (aot == null) {
            throw new ArgumentNullException(nameof(aot));
        }
        return EntitySchemaHolder.Initialize(
            EntitySchemaSource.RegisteredTypes,
            // Only called to validate a tolerated repeat, never on the creating path.
            aot.DescribeRegisteredTypes,
            // NativeAOT.CreateSchema seals through EntitySchemaHolder.Set.
            () => aot.CreateSchema());
    }

    /// <summary>
    /// Creates the schema by scanning the assemblies currently loaded for <see cref="IComponent"/>,
    /// <see cref="ITag"/> and <see cref="Script"/> types, unless it already exists.
    /// <para>
    /// Call this only once every mod assembly is loaded. Types in assemblies loaded afterwards are absent
    /// from the schema permanently, and using one then throws from <see cref="StructInfo{T}.Index"/>.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A schema already exists and this call is not a permitted
    /// repeat: it came from the other mechanism, repeats are not allowed, or the shapes differ.</exception>
    public static EntitySchema InitializeFromLoadedAssemblies()
    {
        return EntitySchemaHolder.Initialize(
            EntitySchemaSource.LoadedAssemblies,
            // Only called to validate a tolerated repeat. It rescans, which is the price of checking that a
            // repeat would have produced the same schema, and is paid only on that path.
            static () => EntitySchemaHolder.DescribeShape(SchemaUtils.CreateSchemaByReflection()),
            // The scan runs only when this call is the one creating the schema, so the common path does not
            // pay for walking and force-loading the whole reference graph twice.
            static () => EntitySchemaHolder.Set(
                SchemaUtils.CreateSchemaByReflection(), EntitySchemaSource.LoadedAssemblies));
    }
}
