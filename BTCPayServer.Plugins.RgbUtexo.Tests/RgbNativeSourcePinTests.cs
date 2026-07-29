using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Pins properties of the startup self-check that no runtime assertion can reach: which member a
/// name resolves to, which default a production call site takes, and what the probe path may not
/// touch. Every clause here follows the five standing rules — node assertions only, no shadowing,
/// no reassignment, no conditional compilation, and semantic binding through a real compilation.
/// </summary>
public class RgbNativeSourcePinTests
{
    [Fact]
    public void PluginSources_ContainNoConditionalCompilationOrAliases()
    {
        RoslynPins.AssertNoDirectivesOrAliases(PluginCompilation.Shared);
    }

    [Fact]
    public void PluginSources_DeclarePinnedNamesExactlyAsMandated()
    {
        RoslynPins.AssertRepoWideDeclarationTotals(PluginCompilation.Shared);
    }

    [Fact]
    public void PluginStartup_InvokesLogOnlyEntryPoint()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RoslynPins.PluginFile);
        RoslynPins.AssertDeclarationCounts(tree, new Dictionary<string, int>());

        var execute = RoslynPins.Method(tree, "RGBPlugin", "Execute");
        RoslynPins.AssertNoLocalShadow(execute, "VerifyOrLog");
        RoslynPins.AssertNeverReassigned(execute, "ctx");

        var statements = Assert.IsType<BlockSyntax>(execute.Body).Statements;

        var probes = statements
            .Select((statement, index) => (statement, index))
            .Where(x => x.statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation }
                        && NameOf(invocation) == "VerifyOrLog")
            .ToList();
        Assert.True(probes.Count == 1,
            $"RGBPlugin.Execute must contain exactly one live VerifyOrLog statement directly in its body, found {probes.Count}");

        var probeIndex = probes[0].index;
        var probeCall = (InvocationExpressionSyntax)((ExpressionStatementSyntax)probes[0].statement).Expression;

        var qualifier = Assert.IsType<MemberAccessExpressionSyntax>(probeCall.Expression);
        Assert.True(qualifier.Expression is IdentifierNameSyntax { Identifier.ValueText: "RgbNativeSelfCheck" },
            $"the probe call must be member-access-qualified with RgbNativeSelfCheck, found '{qualifier.Expression}'");

        var arguments = probeCall.ArgumentList.Arguments;
        Assert.True(arguments.Count == 1,
            $"the probe must be called with exactly ctx.BootstrapServices — no probe/hasExport/sink override, found {arguments.Count} argument(s)");
        Assert.Null(arguments[0].NameColon);
        var argument = Assert.IsType<MemberAccessExpressionSyntax>(arguments[0].Expression);
        Assert.Equal("BootstrapServices", argument.Name.Identifier.ValueText);
        Assert.True(argument.Expression is IdentifierNameSyntax { Identifier.ValueText: "ctx" },
            $"the probe argument must be ctx.BootstrapServices, found '{argument}'");

        for (var i = 0; i < probeIndex; i++)
        {
            Assert.False(ContainsReturn(statements[i]),
                $"RGBPlugin.Execute returns (statement {i}) before the self-check — a degraded startup would skip the diagnostic");
        }

        var loadConfiguration = statements
            .Select((statement, index) => (statement, index))
            .Where(x => x.statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Any(invocation => NameOf(invocation) == "LoadConfiguration"))
            .Select(x => x.index)
            .ToList();
        Assert.True(loadConfiguration.Count == 1,
            $"expected exactly one statement invoking LoadConfiguration, found {loadConfiguration.Count}");
        Assert.True(probeIndex < loadConfiguration[0],
            "the self-check must run before LoadConfiguration, whose uncaught failures would otherwise skip it");

        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, probeCall));
        Assert.Equal("VerifyOrLog", symbol.Name);
        Assert.Equal(RoslynPins.SelfCheckType, symbol.ContainingType.ToDisplayString());
    }

    [Fact]
    public void ResolveNative_DelegatesToSharedCandidateLoop()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RoslynPins.VerifyNativeFile);
        RoslynPins.AssertDeclarationCounts(tree, new Dictionary<string, int>
        {
            ["TryLoadFromCandidates"] = 1,
            ["ResolveBaseDir"] = 1,
        });

        var resolve = RoslynPins.Method(tree, "RgbVerifyNative", "ResolveNative");
        RoslynPins.AssertNoLocalShadow(resolve, "TryLoadFromCandidates", "ResolveBaseDir");
        RoslynPins.AssertNeverReassigned(resolve, "assembly", "libraryName");

        var block = Assert.IsType<BlockSyntax>(resolve.Body);
        var guard = Assert.IsType<IfStatementSyntax>(block.Statements[0]);
        var condition = Assert.IsType<BinaryExpressionSyntax>(guard.Condition);
        Assert.True(condition.IsKind(SyntaxKind.NotEqualsExpression),
            $"the first statement must be the libraryName != Library guard, found '{guard.Condition}'");
        Assert.True(condition.Left is IdentifierNameSyntax { Identifier.ValueText: "libraryName" }
                    && condition.Right is IdentifierNameSyntax { Identifier.ValueText: "Library" },
            $"the guard must compare libraryName against Library, found '{guard.Condition}'");
        Assert.NotEmpty(guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>());

        Assert.Empty(block.DescendantNodes().Where(node =>
            node is ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax));

        AssertDelegatesToCandidateLoop(plugin, tree, resolve,
            firstArgument: argument =>
            {
                Assert.True(argument.ArgumentList.Arguments.Count == 1
                            && argument.ArgumentList.Arguments[0].Expression
                                is IdentifierNameSyntax { Identifier.ValueText: "assembly" },
                    $"ResolveNative must pass ResolveBaseDir(assembly), found 'ResolveBaseDir({argument.ArgumentList.Arguments})'");
            });

        var selfCheckTree = plugin.Tree(RoslynPins.SelfCheckFile);
        var defaultProbe = RoslynPins.Method(selfCheckTree, "RgbNativeSelfCheck", "DefaultProbe");
        RoslynPins.AssertNoLocalShadow(defaultProbe, "TryLoadFromCandidates", "ResolveBaseDir");

        AssertDelegatesToCandidateLoop(plugin, selfCheckTree, defaultProbe,
            firstArgument: argument =>
            {
                Assert.True(argument.ArgumentList.Arguments.Count == 1, $"found 'ResolveBaseDir({argument.ArgumentList.Arguments})'");
                var assembly = Assert.IsType<MemberAccessExpressionSyntax>(argument.ArgumentList.Arguments[0].Expression);
                Assert.Equal("Assembly", assembly.Name.Identifier.ValueText);
                var typeOf = Assert.IsType<TypeOfExpressionSyntax>(assembly.Expression);
                Assert.True(typeOf.Type is IdentifierNameSyntax { Identifier.ValueText: "RgbVerifyNative" },
                    $"DefaultProbe must resolve the base directory from the plugin's own assembly, found '{assembly}'");
            });
    }

    [Fact]
    public void ConvenienceOverloads_BindTheirDefaultsToTheRealHelpers()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RoslynPins.SelfCheckFile);
        RoslynPins.AssertDeclarationCounts(tree, new Dictionary<string, int>
        {
            ["Verify"] = 2,
            ["VerifyOrLog"] = 2,
            ["DefaultProbe"] = 1,
            ["DefaultHasExport"] = 1,
        });

        foreach (var name in new[] { "Verify", "VerifyOrLog" })
        {
            var overload = RoslynPins.Method(tree, "RgbNativeSelfCheck", name, IsConvenienceOverload);
            RoslynPins.AssertNoLocalShadow(overload, "DefaultProbe", "DefaultHasExport", name);
            RoslynPins.AssertNeverReassigned(overload, "probe", "hasExport", "sink", "sp");

            var body = RoslynPins.BodyOf(overload);

            var delegation = SingleInvocation(body, name);
            var delegated = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, delegation));
            Assert.Equal(name, delegated.Name);
            Assert.Equal(RoslynPins.SelfCheckType, delegated.ContainingType.ToDisplayString());
            Assert.Equal(4, delegated.Parameters.Length);
            Assert.Equal("ILoggerFactory", delegated.Parameters[0].Type.Name);
            Assert.Equal("TextWriter", delegated.Parameters[1].Type.Name);

            AssertCoalescedDefault(plugin, tree, delegation, "probe", "DefaultProbe");
            AssertCoalescedDefault(plugin, tree, delegation, "hasExport", "DefaultHasExport");

            var sinkAssignments = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(assignment => assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "writer" })
                .ToList();
            Assert.True(sinkAssignments.Count == 1,
                $"{name}(IServiceProvider?) must assign 'writer' exactly once, found {sinkAssignments.Count}");
            var coalesce = Assert.IsType<BinaryExpressionSyntax>(sinkAssignments[0].Right);
            Assert.True(coalesce.IsKind(SyntaxKind.CoalesceExpression),
                $"'writer' must be assigned 'sink ?? Console.Error', found '{sinkAssignments[0]}'");
            Assert.True(coalesce.Left is IdentifierNameSyntax { Identifier.ValueText: "sink" },
                $"'writer' must default from the sink parameter, found '{sinkAssignments[0]}'");
            var consoleError = Assert.IsType<MemberAccessExpressionSyntax>(coalesce.Right);
            Assert.True(RoslynPins.NamesBclMember(consoleError, "Console", "Error"),
                $"the default sink must be Console.Error, found '{consoleError}'");
            RoslynPins.AssertSingleAssignmentTo(overload, "writer", sinkAssignments[0]);
        }
    }

    [Fact]
    public void DefaultHelpers_AreNothingButTheirDelegation()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RoslynPins.SelfCheckFile);

        var probe = RoslynPins.Method(tree, "RgbNativeSelfCheck", "DefaultProbe");
        var probeBody = AssertExpressionBodiedInvocation(probe);
        var probeTarget = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, probeBody));
        Assert.Equal("TryLoadFromCandidates", probeTarget.Name);
        Assert.Equal(RoslynPins.VerifyNativeType, probeTarget.ContainingType.ToDisplayString());

        var hasExport = RoslynPins.Method(tree, "RgbNativeSelfCheck", "DefaultHasExport");
        var hasExportBody = AssertExpressionBodiedInvocation(hasExport);
        var exportAccess = Assert.IsType<MemberAccessExpressionSyntax>(hasExportBody.Expression);
        Assert.True(RoslynPins.NamesBclMember(exportAccess, "NativeLibrary", "TryGetExport"),
            $"DefaultHasExport must delegate to NativeLibrary.TryGetExport, found '{exportAccess}'");
    }

    // The assembly-scoped NativeLibrary.Load overload throws instead of returning IntPtr.Zero, so
    // swapping it in converts an absent or corrupt native (states 1-2) into a self-check fault
    // (state 5) and makes the live resolver throw. No behavioural test reaches it: T18 injects a
    // loader and the healthy-host cases are green either way. Scanned over the whole compilation
    // because a partial class in an unparsed file defeats a per-file check.
    [Fact]
    public void PluginSources_NeverNameTheThrowingNativeLibraryLoadOverload()
    {
        var plugin = PluginCompilation.Shared;
        RoslynPins.AssertNoDirectivesOrAliases(plugin);

        var offenders = plugin.AllTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Where(access => RoslynPins.NamesBclMember(access, "NativeLibrary", "Load"))
                .Select(access => $"{tree.FilePath}: {access}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"NativeLibrary.Load must never be used — it throws where TryLoad returns false: {string.Join("; ", offenders)}");
    }

    // The probe resolves a handle and checks exports; it must never call one. Every export returns
    // CResultString by value and the binding dereferences and frees the returned pointer, which
    // against an ABI-mismatched image can abort the process — at plugin load, on every install,
    // turning a diagnostic into an outage.
    [Fact]
    public void StartupSelfCheckPath_NeverInvokesAnExportedNativeFunction()
    {
        var plugin = PluginCompilation.Shared;
        var selfCheck = plugin.Compilation.GetTypeByMetadataName(RoslynPins.SelfCheckType);
        Assert.NotNull(selfCheck);

        var roots = new[] { "Verify", "VerifyOrLog", "DefaultProbe", "DefaultHasExport" }
            .SelectMany(name => selfCheck!.GetMembers(name).OfType<IMethodSymbol>())
            .ToList();
        Assert.Equal(6, roots.Count);

        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<IMethodSymbol>(roots);
        var reachedExports = new List<string>();

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current)) continue;

            if (current.Name.StartsWith("rgbverify_", StringComparison.Ordinal))
            {
                reachedExports.Add(current.ToDisplayString());
                continue;
            }

            foreach (var edge in Edges(plugin, current)) pending.Enqueue(edge);
        }

        Assert.True(reachedExports.Count == 0,
            $"the startup self-check path reaches native export(s): {string.Join(", ", reachedExports)}");
    }

    static bool IsConvenienceOverload(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Count == 4
        && method.ParameterList.Parameters[0].Identifier.ValueText == "sp";

    static void AssertCoalescedDefault(PluginCompilation plugin, SyntaxTree tree,
        InvocationExpressionSyntax delegation, string parameter, string helper)
    {
        var matches = delegation.ArgumentList.Arguments
            .Select(argument => argument.Expression)
            .OfType<BinaryExpressionSyntax>()
            .Where(binary => binary.IsKind(SyntaxKind.CoalesceExpression)
                             && binary.Left is IdentifierNameSyntax identifier
                             && identifier.Identifier.ValueText == parameter)
            .ToList();
        Assert.True(matches.Count == 1,
            $"the delegation must pass '{parameter} ?? {helper}', found {matches.Count} coalescing argument(s) on '{parameter}'");

        var right = Assert.IsType<IdentifierNameSyntax>(matches[0].Right);
        Assert.Equal(helper, right.Identifier.ValueText);

        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, right));
        Assert.Equal(helper, symbol.Name);
        Assert.Equal(RoslynPins.SelfCheckType, symbol.ContainingType.ToDisplayString());
    }

    static void AssertDelegatesToCandidateLoop(PluginCompilation plugin, SyntaxTree tree,
        MethodDeclarationSyntax method, Action<InvocationExpressionSyntax> firstArgument)
    {
        var call = SingleInvocation(RoslynPins.BodyOf(method), "TryLoadFromCandidates");

        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, call));
        Assert.Equal("TryLoadFromCandidates", symbol.Name);
        Assert.Equal(RoslynPins.VerifyNativeType, symbol.ContainingType.ToDisplayString());

        Assert.DoesNotContain(call.ArgumentList.Arguments,
            argument => argument.NameColon?.Name.Identifier.ValueText == "load");
        Assert.True(call.ArgumentList.Arguments.Count == 5,
            $"the shared loop must be called with baseDir plus the four out-values and no loader override, found {call.ArgumentList.Arguments.Count} argument(s)");

        var baseDir = Assert.IsType<InvocationExpressionSyntax>(call.ArgumentList.Arguments[0].Expression);
        Assert.Equal("ResolveBaseDir", NameOf(baseDir));
        var baseDirSymbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, baseDir));
        Assert.Equal(RoslynPins.VerifyNativeType, baseDirSymbol.ContainingType.ToDisplayString());
        firstArgument(baseDir);
    }

    static InvocationExpressionSyntax AssertExpressionBodiedInvocation(MethodDeclarationSyntax method)
    {
        Assert.Null(method.Body);
        var arrow = method.ExpressionBody;
        Assert.NotNull(arrow);
        var invocation = Assert.IsType<InvocationExpressionSyntax>(arrow!.Expression);
        Assert.Empty(arrow.DescendantNodes().OfType<LiteralExpressionSyntax>());
        Assert.Empty(arrow.DescendantNodes().OfType<ReturnStatementSyntax>());
        return invocation;
    }

    static InvocationExpressionSyntax SingleInvocation(SyntaxNode body, string name)
    {
        var matches = body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(invocation => NameOf(invocation) == name)
            .ToList();
        Assert.True(matches.Count == 1,
            $"expected exactly one invocation of '{name}', found {matches.Count}");
        return matches[0];
    }

    static bool ContainsReturn(SyntaxNode statement) =>
        statement.DescendantNodesAndSelf()
            .Where(node => node is ReturnStatementSyntax)
            .Any(node => node.Ancestors()
                .TakeWhile(ancestor => ancestor != statement.Parent)
                .All(ancestor => ancestor is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax
                    or LocalFunctionStatementSyntax)));

    static IEnumerable<IMethodSymbol> Edges(PluginCompilation plugin, IMethodSymbol method)
    {
        foreach (var symbol in ReferencedMethods(plugin, method.DeclaringSyntaxReferences))
            yield return symbol;

        var type = method.ContainingType;
        if (type == null) yield break;

        foreach (var constructor in type.StaticConstructors)
        {
            foreach (var symbol in ReferencedMethods(plugin, constructor.DeclaringSyntaxReferences))
                yield return symbol;
        }

        var initialised = type.GetMembers()
            .Where(member => member is IFieldSymbol or IPropertySymbol)
            .SelectMany(member => member.DeclaringSyntaxReferences);
        foreach (var symbol in ReferencedMethods(plugin, initialised))
            yield return symbol;
    }

    static IEnumerable<IMethodSymbol> ReferencedMethods(PluginCompilation plugin,
        IEnumerable<SyntaxReference> references)
    {
        foreach (var reference in references)
        {
            var node = reference.GetSyntax();
            if (!plugin.Compilation.SyntaxTrees.Contains(node.SyntaxTree)) continue;
            var model = plugin.Model(node.SyntaxTree);

            foreach (var descendant in node.DescendantNodesAndSelf())
            {
                if (descendant is not (IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax))
                    continue;
                if (model.GetSymbolInfo(descendant).Symbol is IMethodSymbol referenced)
                    yield return referenced.OriginalDefinition;
            }
        }
    }

    static string NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty
    };
}
