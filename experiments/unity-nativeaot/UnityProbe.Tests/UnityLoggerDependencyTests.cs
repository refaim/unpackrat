using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;
using Cpp2IlLogger = Cpp2IL.Core.Logging.Logger;
using SdkILogger = AssetRipper.Import.Logging.ILogger;
using SdkLogCategory = AssetRipper.Import.Logging.LogCategory;
using SdkLogger = AssetRipper.Import.Logging.Logger;
using SdkLogType = AssetRipper.Import.Logging.LogType;

namespace Unpackrat.UnityProbe.Tests;

internal static class UnityLoggerDependencyCollectionName
{
    public const string Value = "Unpackrat.UnityProbe.Tests.UnityLoggerDependency";
}

[CollectionDefinition(UnityLoggerDependencyCollectionName.Value, DisableParallelization = true)]
public sealed class UnityLoggerDependencyCollection
{
}

[Collection(UnityLoggerDependencyCollectionName.Value)]
public sealed class UnityLoggerDependencyTests
{
    private const string Source = "unity-logger-dependency-test";

    [Fact]
    public void ActivatingIl2CppManager_ForwardsCpp2IlEventsToSdkLoggers()
    {
        bool originalAllowVerbose = SdkLogger.AllowVerbose;
        var capture = new CapturingLogger();
        bool added = false;

        try
        {
            RuntimeHelpers.RunClassConstructor(
                typeof(AssetRipper.Import.Structure.Assembly.Managers.IL2CppManager).TypeHandle);

            SdkLogger.Add(capture);
            added = true;
            SdkLogger.AllowVerbose = true;

            int baseline = capture.Snapshot().Length;

            Cpp2IlLogger.Info("  info-payload  ", Source);
            Cpp2IlLogger.Warn("  warn-payload  ", Source);
            Cpp2IlLogger.Error("  error-payload  ", Source);
            Cpp2IlLogger.Verbose("  verbose-payload  ", Source);

            CapturedLog[] entries = capture.Snapshot().Skip(baseline).ToArray();
            Assert.Equal(4, entries.Length);
            AssertMapped(entries[0], SdkLogType.Info, "info-payload");
            AssertMapped(entries[1], SdkLogType.Verbose, "warn-payload");
            AssertMapped(entries[2], SdkLogType.Error, "error-payload");
            AssertMapped(entries[3], SdkLogType.Verbose, "verbose-payload");
        }
        finally
        {
            SdkLogger.AllowVerbose = originalAllowVerbose;
            if (added)
            {
                SdkLogger.Remove(capture);
            }
        }
    }

    [Fact]
    public void ActivatingIl2CppManagerTwice_DoesNotDuplicateCpp2IlForwarding()
    {
        bool originalAllowVerbose = SdkLogger.AllowVerbose;
        var capture = new CapturingLogger();
        bool added = false;

        try
        {
            RuntimeHelpers.RunClassConstructor(
                typeof(AssetRipper.Import.Structure.Assembly.Managers.IL2CppManager).TypeHandle);

            SdkLogger.Add(capture);
            added = true;
            SdkLogger.AllowVerbose = true;

            int baseline = capture.Snapshot().Length;

            Cpp2IlLogger.Info("first-payload", Source);
            CapturedLog[] afterFirst = capture.Snapshot().Skip(baseline).ToArray();
            Assert.Single(afterFirst);
            AssertMapped(afterFirst[0], SdkLogType.Info, "first-payload");

            RuntimeHelpers.RunClassConstructor(
                typeof(AssetRipper.Import.Structure.Assembly.Managers.IL2CppManager).TypeHandle);

            Cpp2IlLogger.Info("second-payload", Source);
            CapturedLog[] afterSecond = capture.Snapshot().Skip(baseline).ToArray();
            Assert.Equal(2, afterSecond.Length);
            AssertMapped(afterSecond[0], SdkLogType.Info, "first-payload");
            AssertMapped(afterSecond[1], SdkLogType.Info, "second-payload");
        }
        finally
        {
            SdkLogger.AllowVerbose = originalAllowVerbose;
            if (added)
            {
                SdkLogger.Remove(capture);
            }
        }
    }

    [Fact]
    public void OrdinarySdkLoggerInfo_DoesNotLoadCpp2IlDependenciesIntoOwnedLoadContext()
    {
        string sdkAssemblyPath = typeof(SdkLogger).Assembly.Location;
        Assert.False(string.IsNullOrWhiteSpace(sdkAssemblyPath), "SDK assembly location is unavailable.");
        Assert.True(File.Exists(sdkAssemblyPath), $"SDK assembly not found at '{sdkAssemblyPath}'.");

        string sdkDirectory = Path.GetDirectoryName(sdkAssemblyPath)!;
        var context = new IsolatedSdkLoadContext(sdkDirectory);

        try
        {
            Assembly sdkAssembly = context.LoadFromAssemblyPath(sdkAssemblyPath);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(sdkAssembly));
            Assert.Contains(sdkAssembly, context.Assemblies);

            Type? loggerType = sdkAssembly.GetType("AssetRipper.Import.Logging.Logger", throwOnError: false);
            Assert.NotNull(loggerType);

            MethodInfo? info = loggerType!.GetMethod(
                "Info",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);
            Assert.NotNull(info);

            info!.Invoke(null, new object[] { "resource-only" });

            Assert.Null(context.FirstMissingCpp2IlDependency);

            string[] loadedNames = context.Assemblies
                .Select(assembly => assembly.GetName().Name ?? string.Empty)
                .ToArray();
            string[] forbidden = loadedNames.Where(IsCpp2IlDependencyName).ToArray();

            Assert.True(
                forbidden.Length == 0,
                $"Ordinary SDK logger use loaded forbidden Cpp2IL dependencies into the owned load context: {string.Join(", ", forbidden)}. Loaded: {string.Join(", ", loadedNames)}");
        }
        finally
        {
            context.Unload();
        }
    }

    private static void AssertMapped(CapturedLog entry, SdkLogType expectedType, string expectedPayload)
    {
        Assert.Equal(expectedType, entry.Type);
        Assert.Equal(SdkLogCategory.Cpp2IL, entry.Category);
        Assert.Contains(expectedPayload, entry.Message);
        Assert.Equal(entry.Message.Trim(), entry.Message);
    }

    private static bool IsCpp2IlDependencyName(string name)
    {
        return name.Equals("Cpp2IL.Core", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LibCpp2IL", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AsmResolver", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Iced", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapturingLogger : SdkILogger
    {
        private readonly object _gate = new();
        private readonly List<CapturedLog> _entries = new();

        public void Log(SdkLogType type, SdkLogCategory category, string message)
        {
            lock (_gate)
            {
                _entries.Add(new CapturedLog(type, category, message));
            }
        }

        public void BlankLine(int numLines)
        {
        }

        public CapturedLog[] Snapshot()
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    private readonly record struct CapturedLog(SdkLogType Type, SdkLogCategory Category, string Message);

    private sealed class IsolatedSdkLoadContext : AssemblyLoadContext
    {
        private static readonly HashSet<string> RuntimeOwnedAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib",
            "netstandard",
            "System.Private.CoreLib",
            "System.Runtime",
            "System.Runtime.InteropServices",
        };

        private readonly string _directory;

        public IsolatedSdkLoadContext(string directory)
            : base("Unpackrat.UnityProbe.LoggerIsolation", isCollectible: true)
        {
            _directory = directory;
        }

        public string? FirstMissingCpp2IlDependency { get; private set; }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? name = assemblyName.Name;
            if (string.IsNullOrEmpty(name) || RuntimeOwnedAssemblies.Contains(name))
            {
                return null;
            }

            string candidate = Path.Combine(_directory, name + ".dll");
            if (File.Exists(candidate))
            {
                return LoadFromAssemblyPath(candidate);
            }

            if (IsCpp2IlDependencyName(name) && FirstMissingCpp2IlDependency is null)
            {
                FirstMissingCpp2IlDependency = name;
            }

            return null;
        }
    }
}