using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Pins the wiring of the automatic UTXO-replenishment path. No test in this codebase constructs the
/// listener, so without these the shell could drop back to counting every Pending row, or ignore the
/// decision it just computed, while the whole suite stayed green.
///
/// Scope, stated so nobody mistakes it: these catch an ACCIDENTAL regression of the wiring — a refactor,
/// a merge, a well-meaning simplification. They are not a defence against a committer who intends to
/// remove the control, because whoever can edit the method can edit the pin. That case is caught by code
/// review and by the live end-to-end run.
/// </summary>
public class RgbListenerSourcePinTests
{
    const string ListenerFile = "Services/RGBInvoiceListener.cs";
    const string RgbLibFile = "Services/RgbLibService.cs";
    const string ListenerType = "RGBInvoiceListener";
    const string Replenish = "ReplenishUtxosAsync";

    static MethodDeclarationSyntax ReplenishMethod(PluginCompilation plugin) =>
        RoslynPins.Method(plugin.Tree(ListenerFile), ListenerType, Replenish);

    static string NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => string.Empty
    };

    static List<InvocationExpressionSyntax> InvocationsNamed(SyntaxNode scope, string name) =>
        scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == name)
            .ToList();

    static List<InvocationExpressionSyntax> RepoWideInvocationsNamed(PluginCompilation plugin, string name) =>
        plugin.AllTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(i => NameOf(i) == name)
            .ToList();

    static InvocationExpressionSyntax Single(SyntaxNode scope, string name)
    {
        var found = InvocationsNamed(scope, name);
        Assert.True(found.Count == 1, $"expected exactly one '{name}' invocation, found {found.Count}");
        return found[0];
    }

    static string ContainingMethod(SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "<none>";

    static ExpressionSyntax Unwrap(ExpressionSyntax expression) => expression switch
    {
        AwaitExpressionSyntax await_ => Unwrap(await_.Expression),
        ParenthesizedExpressionSyntax paren => Unwrap(paren.Expression),
        _ => expression
    };

    /// <summary>The single declarator's initializer for a local, with `await` unwrapped.</summary>
    static ExpressionSyntax InitializerOf(MethodDeclarationSyntax method, string localName)
    {
        var declarators = RoslynPins.BodyOf(method).DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText == localName)
            .ToList();
        Assert.True(declarators.Count == 1,
            $"expected exactly one declarator named '{localName}' in {Replenish}, found {declarators.Count}");
        var value = declarators[0].Initializer?.Value;
        Assert.True(value != null, $"'{localName}' has no initializer");
        return Unwrap(value!);
    }

    /// <summary>
    /// Pins which member produced a local, and optionally on which receiver. `List&lt;T&gt;.Count` is a
    /// property, so the initializer may be a member access rather than an invocation; both are accepted.
    /// The receiver matters: without it, `colorableCount = walletIds.Count` satisfies "produced by Count"
    /// while making the signing target depend on how many wallets exist rather than on colorable UTXOs.
    /// </summary>
    /// <summary>
    /// Pins the WHOLE call chain from the named root to the producer, not just its two ends.
    ///
    /// WHY the intermediates are enumerated rather than skipped over: an earlier version resolved the
    /// receiver by recursing through any intervening invocation, so it pinned only the ROOT of a chain.
    /// `var nowUnix = now.AddMinutes(-30).ToUnixTimeSeconds();` satisfied "produced by ToUnixTimeSeconds,
    /// from now" while shifting the clock back half an hour, which makes rows that expired in the last
    /// 30 minutes count as active and raises automatic signing demand — a false-ACCEPT on audit clause 2,
    /// using the exact arithmetic the live E2E used to prove that clause. `colorable.Take(1).Count()`
    /// slipped through the same gap. Any hop not named in <paramref name="through"/> is now a failure.
    /// </summary>
    static void AssertProducedBy(MethodDeclarationSyntax method, string localName, string producer,
        string? receiver = null, params string[] through)
    {
        var initializer = InitializerOf(method, localName);
        var (actual, tail) = initializer switch
        {
            InvocationExpressionSyntax invocation => (NameOf(invocation), InnerOf(invocation.Expression)),
            MemberAccessExpressionSyntax access => (access.Name.Identifier.ValueText, access.Expression),
            _ => (initializer.ToString(), null)
        };
        Assert.True(actual == producer, $"'{localName}' must be produced by {producer}, found '{actual}'");
        if (receiver == null) return;

        var hops = new List<string>();
        while (tail is InvocationExpressionSyntax hop)
        {
            hops.Add(NameOf(hop));
            tail = InnerOf(hop.Expression);
        }

        var root = (tail as IdentifierNameSyntax)?.Identifier.ValueText;
        Assert.True(root == receiver,
            $"'{localName}' must be produced from '{receiver}', found '{root ?? tail?.ToString() ?? "<none>"}'");
        Assert.True(hops.SequenceEqual(through),
            $"'{localName}' must reach '{receiver}' through [{string.Join(", ", through)}], "
            + $"found [{string.Join(", ", hops)}] — an unpinned hop can transform the pinned input");
    }

    /// <summary>The expression a member access is invoked on, or null if the shape is not a member access.</summary>
    static ExpressionSyntax? InnerOf(ExpressionSyntax expression) =>
        expression is MemberAccessExpressionSyntax access ? access.Expression : null;

    static ArgumentSyntax NamedArgument(InvocationExpressionSyntax invocation, string name)
    {
        var argument = invocation.ArgumentList.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name);
        Assert.True(argument != null,
            $"'{NameOf(invocation)}' must pass '{name}' as a named argument; found: "
            + string.Join(", ", invocation.ArgumentList.Arguments.Select(a => a.NameColon?.Name.Identifier.ValueText ?? "<positional>")));
        return argument!;
    }

    /// <summary>
    /// Pins a named argument to a member access — both the bound symbol AND the receiver it is read from.
    /// The receiver is not optional: `new RGBConfiguration().MaxAutoColorableUtxos` binds to exactly the
    /// same symbol as `_cfg.MaxAutoColorableUtxos` while ignoring the operator's configured cap, and
    /// `walletIds.Count` has the same leaf name as `colorable.Count`. Binding the leaf alone pins nothing.
    /// </summary>
    static void AssertArgumentBindsTo(PluginCompilation plugin, SyntaxTree tree,
        InvocationExpressionSyntax invocation, string parameter, string containingType, string member,
        string receiver)
    {
        var expression = NamedArgument(invocation, parameter).Expression;
        var access = Assert.IsType<MemberAccessExpressionSyntax>(expression);
        var symbol = RoslynPins.BoundSymbol(plugin, tree, access);
        Assert.True(symbol.Name == member && symbol.ContainingType?.Name == containingType,
            $"'{parameter}:' must bind to {containingType}.{member}, found "
            + $"{symbol.ContainingType?.Name}.{symbol.Name}");
        var actualReceiver = Assert.IsType<IdentifierNameSyntax>(access.Expression).Identifier.ValueText;
        Assert.True(actualReceiver == receiver,
            $"'{parameter}:' must be read from '{receiver}', found '{actualReceiver}'");
    }

    static void AssertArgumentIsLocal(PluginCompilation plugin, SyntaxTree tree,
        InvocationExpressionSyntax invocation, string parameter, string localName)
    {
        var expression = NamedArgument(invocation, parameter).Expression;
        var identifier = Assert.IsType<IdentifierNameSyntax>(expression);
        Assert.Equal(localName, identifier.Identifier.ValueText);
        Assert.IsAssignableFrom<ILocalSymbol>(RoslynPins.BoundSymbol(plugin, tree, identifier));
    }

    // ---- P-C1: clause 1, the enabled gate --------------------------------------------------------

    [Fact]
    public void PC1_TheOnlyArgumentBearingPaymentMethodConfigsLookup_AsksForEnabledOnly()
    {
        var plugin = PluginCompilation.Shared;
        var all = RepoWideInvocationsNamed(plugin, "GetPaymentMethodConfigs");
        Assert.True(all.Count == 5,
            $"the plugin has {all.Count} GetPaymentMethodConfigs invocations; the mandated total is 5 — "
            + "a new call site must be reviewed against finding C before this count is updated");

        var argumentBearing = all.Where(i => i.ArgumentList.Arguments.Count > 0).ToList();
        Assert.True(argumentBearing.Count == 1,
            $"exactly one GetPaymentMethodConfigs call may pass an argument, found {argumentBearing.Count}");

        var call = argumentBearing[0];
        Assert.Equal(Replenish, ContainingMethod(call));
        var argument = call.ArgumentList.Arguments[0];
        var literal = Assert.IsType<LiteralExpressionSyntax>(argument.Expression);
        Assert.True(literal.IsKind(SyntaxKind.TrueLiteralExpression),
            "the replenishment sweep must call GetPaymentMethodConfigs(onlyEnabled: true) — "
            + "the default overload returns methods the merchant has excluded");
    }

    // ---- P-C2: clause 2, the active-invoice predicate --------------------------------------------

    [Fact]
    public void PC2_BothPendingCounts_GoThroughTheSharedActivePredicate()
    {
        var plugin = PluginCompilation.Shared;
        var invocations = RepoWideInvocationsNamed(plugin, "ActivePendingInvoicePredicate");
        Assert.True(invocations.Count == 2,
            $"expected exactly two ActivePendingInvoicePredicate invocations, found {invocations.Count}");
        Assert.Equal(
            new[] { "Utxos", Replenish }.OrderBy(x => x, StringComparer.Ordinal),
            invocations.Select(ContainingMethod).OrderBy(x => x, StringComparer.Ordinal));

        // The absence claim is scoped to the provably-unique declaration: RGBInvoiceListener is not
        // partial, and seven other RGBInvoiceStatus.Pending references legitimately exist elsewhere.
        var replenish = ReplenishMethod(plugin);
        var pendingReferences = RoslynPins.BodyOf(replenish).DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(m => RoslynPins.NamesBclMember(m, "RGBInvoiceStatus", "Pending"))
            .ToList();
        Assert.True(pendingReferences.Count == 0,
            $"{Replenish} must not test RGBInvoiceStatus.Pending inline — that is the unfiltered count "
            + $"finding C is about; found {pendingReferences.Count}");
    }

    // ---- P-C3: the cheap gates precede the expensive call ----------------------------------------

    [Fact]
    public void PC3_EligibilityIsDecidedBeforeAnyRgbLibWork()
    {
        var plugin = PluginCompilation.Shared;
        var replenish = ReplenishMethod(plugin);
        var eligibility = Single(replenish, "EvaluateReplenishEligibility");
        var listUnspents = Single(replenish, "ListUnspentsAsync");
        Assert.True(eligibility.SpanStart < listUnspents.SpanStart,
            "the eligibility gates must run before ListUnspentsAsync, so a wallet whose store never "
            + "enabled RGB costs no rgb-lib work");
    }

    // ---- P-C4: the signing call's arguments ------------------------------------------------------

    [Fact]
    public void PC4_TheCreationRequestsExactlyWhatTheDemandFunctionDecided()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var replenish = ReplenishMethod(plugin);

        Single(replenish, "EvaluateReplenishDemand");
        var create = Single(replenish, "CreateColorableUtxosAsync");

        // Named, because positionally (id, decision.UtxoSize, decision.RequestCount, ct) compiles and
        // asks for 1000 UTXOs — the signature is (walletId, count = 4, size = 1000, ct).
        AssertArgumentBindsTo(plugin, tree, create, "walletId", "RGBWallet", "Id", receiver: "w");
        AssertArgumentBindsTo(plugin, tree, create, "count", "ReplenishDecision", "RequestCount", receiver: "decision");
        AssertArgumentBindsTo(plugin, tree, create, "size", "ReplenishDecision", "UtxoSize", receiver: "decision");

        // Dropping ct would let the creation sign and broadcast during shutdown. It must be THIS method's
        // own CancellationToken parameter — any parameter symbol would also be satisfied by an added
        // `signingCt = default`, which is never cancelled.
        var ctIdentifier = Assert.IsType<IdentifierNameSyntax>(NamedArgument(create, "ct").Expression);
        var ctSymbol = Assert.IsAssignableFrom<IParameterSymbol>(RoslynPins.BoundSymbol(plugin, tree, ctIdentifier));
        var declared = plugin.Model(tree).GetDeclaredSymbol(replenish);
        Assert.True(declared != null, $"{Replenish} does not bind to a method symbol");
        // The sweep takes exactly one parameter, and `ct:` must BE it. Checking only "some parameter of this
        // method, of type CancellationToken" is not enough: adding `signingCt = default` and passing that
        // satisfies it while handing the creation a token that is never cancelled.
        Assert.True(declared!.Parameters.Length == 1,
            $"{Replenish} must take exactly one parameter (its CancellationToken), found "
            + $"{declared.Parameters.Length}: {string.Join(", ", declared.Parameters.Select(p => p.Name))}");
        Assert.True(ctSymbol.Equals(declared.Parameters[0], SymbolEqualityComparer.Default),
            $"'ct:' must be {Replenish}'s own cancellation token, found '{ctSymbol.Name}'");
        Assert.True(ctSymbol.Type.Name == "CancellationToken",
            $"'ct:' must be a CancellationToken, found {ctSymbol.Type.Name}");

        // The receiver too: `decision with { RequestCount = 5000 }` would otherwise satisfy the above.
        foreach (var parameter in new[] { "count", "size" })
        {
            var access = (MemberAccessExpressionSyntax)NamedArgument(create, parameter).Expression;
            var receiver = Assert.IsType<IdentifierNameSyntax>(access.Expression);
            Assert.Equal("decision", receiver.Identifier.ValueText);
        }
        AssertProducedBy(replenish, "decision", "EvaluateReplenishDemand");
    }

    // ---- P-C5: no second automatic path, and the tracker is actually wired ------------------------

    [Fact]
    public void PC5_OnlyTheListenerAndTheAdminButtonCreateUtxos()
    {
        var plugin = PluginCompilation.Shared;
        var creations = RepoWideInvocationsNamed(plugin, "CreateColorableUtxosAsync");
        Assert.True(creations.Count == 2,
            $"the plugin has {creations.Count} CreateColorableUtxosAsync invocations; exactly two are "
            + "mandated — the listener's automatic path and the admin Create-UTXOs button");

        var owners = creations
            .Select(c => RoslynPins.BoundSymbol(plugin, c.SyntaxTree, c.Expression).ContainingType?.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "IRGBWalletService", "RGBWalletService" }, owners);

        foreach (var member in new[]
                 {
                     "NextEligibleAt", "RecordAttemptSucceeded", "RecordAttemptFailed",
                     "RecordNoActionNeeded", "Prune"
                 })
        {
            var found = RepoWideInvocationsNamed(plugin, member);
            Assert.True(found.Count == 1,
                $"expected exactly one '{member}' invocation in the plugin, found {found.Count}");
        }
    }

    // ---- P-C6: the decision reads a freshly-read row ---------------------------------------------

    [Fact]
    public void PC6_TheWalletRowIsReReadBeforeTheDecision()
    {
        var plugin = PluginCompilation.Shared;
        var replenish = ReplenishMethod(plugin);
        var fresh = Single(replenish, "FirstOrDefaultAsync");
        var eligibility = Single(replenish, "EvaluateReplenishEligibility");
        Assert.True(fresh.SpanStart < eligibility.SpanStart,
            "the wallet row must be re-read before eligibility is decided — the sweep-start list is a "
            + "snapshot, and a concurrent send can quarantine a wallet inside the same sweep");
    }

    // ---- P-C8: a failed UTXO listing must not look like an empty wallet --------------------------

    [Fact]
    public void PC8_AFailedUnspentsListing_ThrowsRatherThanReportingZeroUtxos()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);
        var list = RoslynPins.Method(tree, "RgbLibService", "ListUnspentsAsync");
        var body = RoslynPins.BodyOf(list);

        // Returning an empty list on a failed native call made an error indistinguishable from "this wallet
        // has no UTXOs". The replenishment sweep then saw zero colorable UTXOs, computed zero free slots and
        // signed a creation *because of the failure* — observed live on 2026-08-04 against a wallet holding
        // 23 UTXOs. A genuinely empty wallet returns Ok with "[]", so a null payload only ever means failure.
        // Pin the null-payload BRANCH, not merely "a throw exists somewhere": this method already contains
        // an offline `?? throw` expression, and accepting any throw would let a silent revert pass.
        var nullChecks = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition is BinaryExpressionSyntax bin
                        && bin.IsKind(SyntaxKind.EqualsExpression)
                        && bin.Left is IdentifierNameSyntax
                        && bin.Right.IsKind(SyntaxKind.NullLiteralExpression))
            .ToList();
        Assert.True(nullChecks.Count == 1,
            $"ListUnspentsAsync must test its payload for null exactly once, found {nullChecks.Count}");
        var guarded = nullChecks[0].Statement;
        var throwsInBranch = guarded is ThrowStatementSyntax
                             || guarded.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any();
        Assert.True(throwsInBranch,
            "the null-payload branch must throw — returning any value there makes a failed native call "
            + "indistinguishable from a wallet with no UTXOs, which drove a real signed creation");

        // …and no value may be produced for that failure, in either the `new List<…>()` or the collection
        // expression form (`return [];`), which is the prevailing style in this very method.
        var manufacturedEmpty = guarded.DescendantNodesAndSelf().Where(n =>
            (n is ObjectCreationExpressionSyntax o && o.Type.ToString().Contains("List<UnspentOutput>"))
            || n is CollectionExpressionSyntax).ToList();
        Assert.True(manufacturedEmpty.Count == 0,
            $"the null-payload branch must not manufacture a UTXO list, found {manufacturedEmpty.Count}");
    }

    // ---- P-C7: provenance, mutation and structure ------------------------------------------------

    [Fact]
    public void PC7_EveryDecisionInputComesFromWhereItMust()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var replenish = ReplenishMethod(plugin);
        var eligibility = Single(replenish, "EvaluateReplenishEligibility");
        var demand = Single(replenish, "EvaluateReplenishDemand");

        // A local function shadowing any pinned name compiles without a warning and satisfies every node
        // assertion below while the real member never runs (phase 1a's standing rule 2b).
        RoslynPins.AssertNoLocalShadow(replenish,
            "EvaluateReplenishEligibility", "EvaluateReplenishDemand", "ActivePendingInvoicePredicate",
            "ListUnspentsAsync", "FirstOrDefaultAsync", "CountAsync", "TryGetValue", "FindStore",
            "GetPaymentMethodConfigs", "CreateColorableUtxosAsync",
            "NextEligibleAt", "RecordAttemptSucceeded", "RecordAttemptFailed", "RecordNoActionNeeded", "Prune");

        // Provenance: each pinned local is produced by the call that must produce it.
        AssertProducedBy(replenish, "w", "FirstOrDefaultAsync");
        AssertProducedBy(replenish, "store", "FindStore");
        AssertProducedBy(replenish, "configs", "GetPaymentMethodConfigs", receiver: "store");
        // `config` is built by the single `enabled && tok is not null ? … : null` declarator. Without this,
        // a config parsed from another store's token supplies that store's UtxoCount/UtxoSize.
        var configInit = InitializerOf(replenish, "config");
        var configTernary = Assert.IsType<ConditionalExpressionSyntax>(configInit);
        // Nodes, not text: a `Contains("enabled")` on the condition's ToString() is exactly the
        // node-not-text evasion the standing rules forbid (a comment or a renamed local defeats it).
        var condition = Assert.IsType<BinaryExpressionSyntax>(configTernary.Condition);
        Assert.True(condition.IsKind(SyntaxKind.LogicalAndExpression),
            $"config's condition must be a logical AND, found {condition.Kind()}");
        var enabledOperand = Assert.IsType<IdentifierNameSyntax>(condition.Left);
        Assert.IsAssignableFrom<ILocalSymbol>(RoslynPins.BoundSymbol(plugin, tree, enabledOperand));
        Assert.Equal("enabled", enabledOperand.Identifier.ValueText);
        var tokPattern = Assert.IsType<IsPatternExpressionSyntax>(condition.Right);
        Assert.Equal("tok", Assert.IsType<IdentifierNameSyntax>(tokPattern.Expression).Identifier.ValueText);
        Assert.Equal("ToObject", NameOf(Assert.IsType<InvocationExpressionSyntax>(configTernary.WhenTrue)));
        Assert.True(configTernary.WhenFalse.IsKind(SyntaxKind.NullLiteralExpression),
            "config must be null when the payment method is not enabled");
        AssertProducedBy(replenish, "enabled", "TryGetValue");
        AssertProducedBy(replenish, "utxos", "ListUnspentsAsync");
        AssertProducedBy(replenish, "colorable", "ToList", receiver: "utxos", through: "Where");
        AssertProducedBy(replenish, "colorableCount", "Count", receiver: "colorable");
        AssertProducedBy(replenish, "usedByColorings", "Sum", receiver: "colorable");
        AssertProducedBy(replenish, "activePendingInvoices", "CountAsync");
        AssertProducedBy(replenish, "nowUnix", "ToUnixTimeSeconds", receiver: "now");
        // `var now = _lastUtxoCheck;` would keep every other pin green while making rows that expired during
        // the previous sweep count as active, raising demand.
        var nowInit = InitializerOf(replenish, "now");
        var nowAccess = Assert.IsType<MemberAccessExpressionSyntax>(nowInit);
        Assert.True(RoslynPins.NamesBclMember(nowAccess, "DateTimeOffset", "UtcNow"),
            $"'now' must be DateTimeOffset.UtcNow, found '{nowInit}'");

        // The store lookup and the config key: a wrong store or a wrong payment method would authorise
        // signing from configuration unrelated to this wallet.
        var findStore = Single(replenish, "FindStore");
        var storeIdAccess = Assert.IsType<MemberAccessExpressionSyntax>(findStore.ArgumentList.Arguments[0].Expression);
        var storeIdSymbol = RoslynPins.BoundSymbol(plugin, tree, storeIdAccess);
        // Leaf name alone would let `otherEntity.StoreId` through and supply another store's UtxoCount.
        Assert.True(storeIdSymbol.Name == "StoreId" && storeIdSymbol.ContainingType?.Name == "RGBWallet",
            $"FindStore's argument must be RGBWallet.StoreId, found "
            + $"{storeIdSymbol.ContainingType?.Name}.{storeIdSymbol.Name}");
        Assert.True(Assert.IsType<IdentifierNameSyntax>(storeIdAccess.Expression).Identifier.ValueText == "w",
            "FindStore's argument must be read from the fresh wallet local 'w'");
        var tryGetValue = Single(replenish, "TryGetValue");
        var keyAccess = Assert.IsType<MemberAccessExpressionSyntax>(tryGetValue.ArgumentList.Arguments[0].Expression);
        var keySymbol = RoslynPins.BoundSymbol(plugin, tree, keyAccess);
        // Bound, not name-matched: `AnyOtherClass.RGBPaymentMethodId` would satisfy a syntactic comparison,
        // which is the standing semantic-binding rule this file is required to obey.
        Assert.True(keySymbol.Name == "RGBPaymentMethodId" && keySymbol.ContainingType?.Name == "RGBPlugin",
            $"the config key must be RGBPlugin.RGBPaymentMethodId, found "
            + $"{keySymbol.ContainingType?.Name}.{keySymbol.Name}");

        // Eligibility's arguments.
        AssertArgumentBindsTo(plugin, tree, eligibility, "walletId", "RGBWallet", "Id", receiver: "w");
        AssertArgumentBindsTo(plugin, tree, eligibility, "isActive", "RGBWallet", "IsActive", receiver: "w");
        AssertArgumentBindsTo(plugin, tree, eligibility, "needsRecovery", "RGBWallet", "NeedsRecovery", receiver: "w");
        AssertArgumentBindsTo(plugin, tree, eligibility, "maxAllocationsPerUtxo", "RGBWallet", "MaxAllocationsPerUtxo", receiver: "w");
        AssertArgumentIsLocal(plugin, tree, eligibility, "paymentMethodEnabled", "enabled");
        AssertArgumentIsLocal(plugin, tree, eligibility, "now", "now");

        // `config?.WalletId`, not `config.WalletId`: config is null on exactly the disabled path this
        // gate exists for, and arguments evaluate before the callee's gates run.
        var configured = NamedArgument(eligibility, "configuredWalletId").Expression;
        var conditional = Assert.IsType<ConditionalAccessExpressionSyntax>(configured);
        Assert.Equal("config", Assert.IsType<IdentifierNameSyntax>(conditional.Expression).Identifier.ValueText);
        Assert.Equal("WalletId",
            Assert.IsType<MemberBindingExpressionSyntax>(conditional.WhenNotNull).Name.Identifier.ValueText);

        // The cooldown read itself, not just the wallet id inside it.
        var nextEligible = NamedArgument(eligibility, "nextEligibleAt").Expression;
        Assert.Equal("NextEligibleAt", NameOf(Assert.IsType<InvocationExpressionSyntax>(nextEligible)));

        // Demand's arguments.
        AssertArgumentIsLocal(plugin, tree, demand, "colorableCount", "colorableCount");
        AssertArgumentIsLocal(plugin, tree, demand, "usedByColorings", "usedByColorings");
        AssertArgumentIsLocal(plugin, tree, demand, "activePendingInvoices", "activePendingInvoices");
        AssertArgumentBindsTo(plugin, tree, demand, "maxAllocationsPerUtxo", "RGBWallet", "MaxAllocationsPerUtxo", receiver: "w");
        AssertArgumentBindsTo(plugin, tree, demand, "minFreeSlots", "RGBPaymentMethodConfig", "UtxoCount", receiver: "config");
        AssertArgumentBindsTo(plugin, tree, demand, "utxoSize", "RGBPaymentMethodConfig", "UtxoSize", receiver: "config");
        AssertArgumentBindsTo(plugin, tree, demand, "maxAutoColorableUtxos", "RGBConfiguration", "MaxAutoColorableUtxos", receiver: "_cfg");

        // The predicate's own two arguments: a literal 0 or another wallet's id reverts clause 2.
        var predicate = Single(replenish, "ActivePendingInvoicePredicate");

        // The predicate invocation must BE the CountAsync argument, not merely exist in the method. Pinning
        // "activePendingInvoices comes from CountAsync" and "a predicate call exists with the right
        // arguments" separately leaves a compiling hole: keep the call as a discard and pass
        // `i => i.WalletId == w.Id` to CountAsync, and every pin stays green while expired and settled rows
        // count toward automatic signing demand — the false-ACCEPT direction, on the audit's own clause 2.
        // One level of naming is allowed — `var p = ActivePendingInvoicePredicate(...); CountAsync(p, ct)`
        // preserves the property exactly — but the value passed must still resolve to the pinned invocation.
        // A pin that fails on a correct refactor teaches people to delete pins.
        var countCall = Assert.IsType<InvocationExpressionSyntax>(
            InitializerOf(replenish, "activePendingInvoices"));
        Assert.Equal("CountAsync", NameOf(countCall));
        var countArgument = countCall.ArgumentList.Arguments[0].Expression;
        if (countArgument is IdentifierNameSyntax named)
            countArgument = InitializerOf(replenish, named.Identifier.ValueText);
        Assert.Same(predicate, countArgument);
        var predicateWallet = Assert.IsType<MemberAccessExpressionSyntax>(predicate.ArgumentList.Arguments[0].Expression);
        var predicateWalletSymbol = RoslynPins.BoundSymbol(plugin, tree, predicateWallet);
        Assert.True(predicateWalletSymbol.Name == "Id" && predicateWalletSymbol.ContainingType?.Name == "RGBWallet",
            "the predicate's wallet id must be RGBWallet.Id");
        Assert.True(Assert.IsType<IdentifierNameSyntax>(predicateWallet.Expression).Identifier.ValueText == "w",
            "the predicate's wallet id must be read from the fresh wallet local 'w'");
        Assert.Equal("nowUnix",
            Assert.IsType<IdentifierNameSyntax>(predicate.ArgumentList.Arguments[1].Expression).Identifier.ValueText);

        // Every LINQ selector: Sum(u => u.RgbAllocations.Count + 1) would inflate demand on every wallet,
        // and Where(u => true) would mis-count the colorable set. The two Where clauses are the sweep's
        // IsActive filter and the colorable filter — both load-bearing, so both are pinned.
        var wheres = InvocationsNamed(RoslynPins.BodyOf(replenish), "Where");
        Assert.True(wheres.Count == 2, $"expected exactly two Where clauses, found {wheres.Count}");
        Assert.Equal(
            new[] { "Colorable", "IsActive" },
            wheres.Select(w => SelectorMember(plugin, tree, w)).OrderBy(x => x, StringComparer.Ordinal));
        // The colorable filter gets the same whole-path treatment as Sum: a leaf named "Colorable" reached
        // from anything other than the lambda's own parameter would mis-count the colorable set.
        AssertSelectorPath(plugin, tree,
            wheres.Single(w => SelectorMember(plugin, tree, w) == "Colorable"), "Utxo", "Colorable");
        AssertSelectorPath(plugin, tree, Single(replenish, "Sum"), "RgbAllocations", "Count");

        // Every wallet-id argument inside the loop is w.Id. Two carve-outs, both structural: the fresh
        // read keys on the loop's id because w is what it produces, and the outer catch logs id because
        // w is scoped inside the try.
        foreach (var call in new[] { "ListUnspentsAsync", "NextEligibleAt", "RecordAttemptSucceeded",
                     "RecordAttemptFailed", "RecordNoActionNeeded" })
        {
            var invocation = Single(replenish, call);
            var access = Assert.IsType<MemberAccessExpressionSyntax>(invocation.ArgumentList.Arguments[0].Expression);
            var symbol = RoslynPins.BoundSymbol(plugin, tree, access);
            // Leaf name alone pins nothing: `store.Id` is also named "Id", and keying the tracker off the
            // store would mean gate 2 never fires for the wallet — the retry storm, with every pin green.
            Assert.True(symbol.Name == "Id" && symbol.ContainingType?.Name == "RGBWallet",
                $"{call}'s wallet id must be RGBWallet.Id, found "
                + $"{symbol.ContainingType?.Name}.{symbol.Name}");
            Assert.True(Assert.IsType<IdentifierNameSyntax>(access.Expression).Identifier.ValueText == "w",
                $"{call}'s wallet id must be read from the fresh wallet local 'w', found '{access.Expression}'");
        }

        // Each Record* reads the clock AT THE MOMENT IT STAMPS, not the decision instant: the wallet's own
        // rgb-lib work can outlast the cooldown, and `now` would then stamp an already-elapsed instant,
        // leaving the wallet immediately eligible again. RecordAttemptFailed(w.Id, DateTimeOffset.MinValue)
        // would do the same thing outright, defeating the backoff while every count still matched.
        foreach (var call in new[] { "RecordAttemptSucceeded", "RecordAttemptFailed", "RecordNoActionNeeded" })
        {
            var stamp = Single(replenish, call).ArgumentList.Arguments[1].Expression;
            var access = Assert.IsType<MemberAccessExpressionSyntax>(stamp);
            Assert.True(RoslynPins.NamesBclMember(access, "DateTimeOffset", "UtcNow"),
                $"{call} must stamp DateTimeOffset.UtcNow read at the call, found '{stamp}'");
        }

        // …and the decision instant must still be captured INSIDE the loop. Hoisting it above the foreach
        // restores the cross-wallet drift: later wallets judged against the sweep's start, counting invoices
        // that expired while it ran.
        var nowDecl = replenish.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "now");
        Assert.True(nowDecl.Ancestors().OfType<ForEachStatementSyntax>().Any(),
            "'now' must be declared inside the per-wallet foreach, not once per sweep");

        // Prune: before the loop, over the very collection the loop iterates. A filtered prune set with an
        // unfiltered work set evicts a wallet immediately before processing it, so NextEligibleAt returns
        // null and its cooldown and backoff are gone — the false-ACCEPT direction.
        var prune = Single(replenish, "Prune");
        var loop = RoslynPins.BodyOf(replenish).DescendantNodes().OfType<ForEachStatementSyntax>().Single();
        Assert.True(prune.SpanStart < loop.SpanStart, "Prune must run before the per-wallet loop");
        var pruneArgument = Assert.IsType<IdentifierNameSyntax>(prune.ArgumentList.Arguments[0].Expression);
        var iterated = Assert.IsType<IdentifierNameSyntax>(loop.Expression);
        Assert.True(pruneArgument.Identifier.ValueText == iterated.Identifier.ValueText,
            $"Prune's argument ('{pruneArgument.Identifier.ValueText}') must be the collection the loop "
            + $"iterates ('{iterated.Identifier.ValueText}')");
        AssertProducedBy(replenish, iterated.Identifier.ValueText, "ToListAsync");

        // No mutation of any pinned value, in any form the harness's own helper cannot see.
        var body = RoslynPins.BodyOf(replenish);
        // Object-initializer assignments (`new Foo { Bar = x }`) are excluded: they populate a fresh object
        // and cannot replace a pinned local, so counting them would fail a correct refactor — the kind of
        // false failure that teaches maintainers to delete pins.
        var mutations = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Parent is not InitializerExpressionSyntax)
            .ToList();
        Assert.True(mutations.Count == 0,
            "ReplenishUtxosAsync must contain no assignment outside an object initializer — every value it "
            + "needs is introduced by a declarator, and an assignment is how a pinned input gets quietly "
            + $"replaced. Found: {string.Join("; ", mutations.Select(m => m.ToString()))}");
        Assert.True(body.DescendantNodes().Count(n =>
            n is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression }
                or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression }) == 0,
            "ReplenishUtxosAsync must contain no ++/-- — activePendingInvoices++ raises demand on every wallet");

        // No entity snapshot to regress to: only the fresh read may be RGBWallet-typed.
        var model = plugin.Model(tree);
        var walletLocals = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Select(v => model.GetDeclaredSymbol(v) as ILocalSymbol)
            .Where(s => s != null && MentionsWallet(s!.Type))
            .Select(s => s!.Name)
            .ToList();
        Assert.Equal(new[] { "w" }, walletLocals);

        // The tracker's construction: FromSeconds, or base and ceiling swapped, collapses the backoff.
        var construction = body.Parent!.Ancestors().OfType<ClassDeclarationSyntax>().First()
            .DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(o => (o.Type as IdentifierNameSyntax)?.Identifier.ValueText == "ReplenishCooldownTracker")
            .ToList();
        Assert.True(construction.Count == 1, $"expected one ReplenishCooldownTracker construction, found {construction.Count}");
        AssertMinutesOf(plugin, tree, construction[0], "baseCooldown", "AutoUtxoCooldownMinutes");
        AssertMinutesOf(plugin, tree, construction[0], "maxBackoff", "AutoUtxoMaxBackoffMinutes");

        // Rebinding the fields is a compile error rather than something a scan must catch.
        foreach (var field in new[] { "_cfg", "_cooldowns" })
            AssertReadonlyField(plugin.Tree(ListenerFile), ListenerType, field);
    }

    static bool MentionsWallet(ITypeSymbol type) =>
        type.Name == "RGBWallet"
        || (type is IArrayTypeSymbol array && MentionsWallet(array.ElementType))
        || (type is INamedTypeSymbol named && named.TypeArguments.Any(MentionsWallet));

    /// <summary>
    /// The member a Where/Sum selector reads. A body that ANDs further conditions onto it — narrowing the
    /// set, e.g. `x => x.IsActive &amp;&amp; x.Network == n` — is accepted, because narrowing can only reduce what
    /// the sweep acts on. `||` is not: widening is how an unpinned wallet re-enters the set. A raw
    /// Assert.IsType here used to fail a correct narrowing with no message at all.
    /// </summary>
    static string SelectorMember(PluginCompilation plugin, SyntaxTree tree, InvocationExpressionSyntax invocation)
    {
        var lambda = Assert.IsType<SimpleLambdaExpressionSyntax>(invocation.ArgumentList.Arguments[0].Expression);
        var candidates = Conjuncts(lambda.Body).OfType<MemberAccessExpressionSyntax>().ToList();
        Assert.True(candidates.Count > 0,
            $"selector '{lambda.Body}' must read a member of '{lambda.Parameter.Identifier.ValueText}', "
            + "optionally ANDed with further narrowing conditions; '||' is not accepted because widening "
            + "lets an unpinned item back into the set");
        return RoslynPins.BoundSymbol(plugin, tree, candidates[0]).Name;
    }

    /// <summary>Splits an `&amp;&amp;` chain into its operands; any other expression is a single operand.</summary>
    static IEnumerable<ExpressionSyntax> Conjuncts(CSharpSyntaxNode body) =>
        body is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } and_
            ? Conjuncts(and_.Left).Concat(Conjuncts(and_.Right))
            : body is ExpressionSyntax e ? [e] : [];

    /// <summary>
    /// Pins a selector's whole path from the lambda's own parameter — `u.Outer.Leaf` — not just the leaf.
    /// `colorable.Sum(u => walletIds.Count)` reads a member named `Count` too, and would inflate demand on
    /// every wallet while a leaf-only assertion stayed green.
    /// </summary>
    static void AssertSelectorPath(PluginCompilation plugin, SyntaxTree tree,
        InvocationExpressionSyntax invocation, string outer, string leaf)
    {
        var lambda = Assert.IsType<SimpleLambdaExpressionSyntax>(invocation.ArgumentList.Arguments[0].Expression);
        // Same contract as SelectorMember: an AND-narrowed body is property-preserving and must pass. Round 9
        // relaxed SelectorMember and left this helper doing a raw cast on the SAME lambda, so a correct
        // narrowing still died here with a message-free type error.
        var leafAccess = Conjuncts(lambda.Body).OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
        Assert.True(leafAccess != null,
            $"the selector '{lambda.Body}' must read a member path from "
            + $"'{lambda.Parameter.Identifier.ValueText}', optionally ANDed with narrowing conditions");
        Assert.True(RoslynPins.BoundSymbol(plugin, tree, leafAccess!).Name == leaf,
            $"the selector's leaf must be '{leaf}', found '{lambda.Body}'");
        var outerAccess = Assert.IsType<MemberAccessExpressionSyntax>(leafAccess!.Expression);
        Assert.True(RoslynPins.BoundSymbol(plugin, tree, outerAccess).Name == outer,
            $"the selector must read '{outer}' before '{leaf}', found '{lambda.Body}'");
        var root = Assert.IsType<IdentifierNameSyntax>(outerAccess.Expression).Identifier.ValueText;
        Assert.True(root == lambda.Parameter.Identifier.ValueText,
            $"the selector must start from the lambda parameter '{lambda.Parameter.Identifier.ValueText}', "
            + $"found '{root}'");
    }

    static void AssertMinutesOf(PluginCompilation plugin, SyntaxTree tree,
        ObjectCreationExpressionSyntax creation, string parameter, string knob)
    {
        var argument = creation.ArgumentList!.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameter);
        Assert.True(argument != null, $"the tracker must be constructed with a named '{parameter}' argument");
        var call = Assert.IsType<InvocationExpressionSyntax>(argument!.Expression);
        Assert.Equal("FromMinutes", NameOf(call));
        var access = Assert.IsType<MemberAccessExpressionSyntax>(call.ArgumentList.Arguments[0].Expression);
        Assert.Equal(knob, RoslynPins.BoundSymbol(plugin, tree, access).Name);
        // The receiver, for the same reason AssertArgumentBindsTo pins one: `new RGBConfiguration().Knob`
        // binds to the identical property symbol while ignoring everything the operator configured. Building
        // the tracker that way would silently run at the 30/160 defaults, so an operator holding unattended
        // signing to once a day would get it every 30 minutes instead.
        var receiver = Assert.IsType<IdentifierNameSyntax>(access.Expression).Identifier.ValueText;
        Assert.True(receiver == "_cfg",
            $"the tracker's '{parameter}' must read {knob} from the injected '_cfg', found '{access.Expression}'");
    }

    static void AssertReadonlyField(SyntaxTree tree, string typeName, string fieldName)
    {
        var field = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.ValueText == typeName)
            .SelectMany(t => t.Members.OfType<FieldDeclarationSyntax>())
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == fieldName));
        Assert.True(field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword),
            $"'{fieldName}' must be readonly so rebinding it is a compile error");
    }

    // P-C9. The two outcome-recording calls must sit in the blocks their names imply. PC5 counts one of each
    // and PC7 pins their arguments, so SWAPPING them — success stamps a failure, the catch stamps a success —
    // leaves every pin and the whole suite green while making a failing creation reset to the base cooldown and
    // a succeeding one back off. That is the retry storm the backoff exists to stop, so it is the permissive
    // direction, and no ablation covered it.
    [Fact]
    public void PC9_OutcomeRecordingSitsInTheBranchItsNameClaims()
    {
        var plugin = PluginCompilation.Shared;
        var replenish = ReplenishMethod(plugin);

        var creation = Single(replenish, "CreateColorableUtxosAsync");
        var creationTry = creation.Ancestors().OfType<TryStatementSyntax>().First();

        var succeeded = Single(replenish, "RecordAttemptSucceeded");
        Assert.Same(creationTry.Block, succeeded.Ancestors().OfType<BlockSyntax>().First());
        Assert.Empty(succeeded.Ancestors().OfType<CatchClauseSyntax>());
        // Position, not just block. Moving the success stamp ABOVE the creation compiles and satisfies every
        // count, argument and block assertion, but each failing creation would then Settle first — clearing
        // _failures — and the catch would re-increment from 1, pinning the backoff at the base forever.
        Assert.True(succeeded.SpanStart > creation.SpanStart,
            "RecordAttemptSucceeded must follow CreateColorableUtxosAsync, not precede it");

        var failed = Single(replenish, "RecordAttemptFailed");
        var catchClause = failed.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
        Assert.True(catchClause != null && catchClause.Parent == creationTry,
            "RecordAttemptFailed must sit in the catch guarding CreateColorableUtxosAsync");

        // …and the no-action stamp must NOT be inside that try/catch, or a skipped wallet would be recorded
        // as an attempt.
        var noAction = Single(replenish, "RecordNoActionNeeded");
        Assert.DoesNotContain(creationTry, noAction.Ancestors());
    }

    // P-C10. Nothing pinned the branch that gates the signing call. Flipping `!=` to `==` compiles, keeps
    // RecordNoActionNeeded present so PC5's counts hold, and routes every refused outcome — SkipCapReached
    // included — into CreateColorableUtxosAsync. Bounded by count: 0, but it is a signing call on a decision
    // that was refused, and it is the most consequential branch in the method.
    [Fact]
    public void PC10_CreationIsGatedOnTheCreateOutcome()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var replenish = ReplenishMethod(plugin);

        var gates = replenish.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition is BinaryExpressionSyntax b
                        && b.Left is MemberAccessExpressionSyntax l
                        && l.Name.Identifier.ValueText == "Outcome")
            .ToList();
        Assert.True(gates.Count == 1, $"expected exactly one decision.Outcome gate, found {gates.Count}");

        var condition = (BinaryExpressionSyntax)gates[0].Condition;
        Assert.True(condition.IsKind(SyntaxKind.NotEqualsExpression),
            $"the creation gate must skip when the outcome is NOT Create, found '{condition}'");

        var left = (MemberAccessExpressionSyntax)condition.Left;
        Assert.Equal("decision", Assert.IsType<IdentifierNameSyntax>(left.Expression).Identifier.ValueText);

        var right = Assert.IsType<MemberAccessExpressionSyntax>(condition.Right);
        var symbol = RoslynPins.BoundSymbol(plugin, tree, right);
        Assert.True(symbol.Name == "Create" && symbol.ContainingType?.Name == "ReplenishOutcome",
            $"the gate must compare against ReplenishOutcome.Create, found "
            + $"{symbol.ContainingType?.Name}.{symbol.Name}");

        // The refused branch must leave the iteration UNCONDITIONALLY. Merely containing a `continue`
        // somewhere is not enough: `if (decision.Outcome != SkipCapReached) continue;` inside the block
        // satisfies containment while letting SkipCapReached fall through to the creation call.
        AssertLeavesTheIterationUnconditionally(gates[0], "the refused-demand gate");

        // …and it must stand BEFORE the creation. Shape alone is not enough: hoisting the creation's
        // try/catch above this gate keeps every pin green while routing SkipCapReached and
        // SkipEnoughFreeSlots straight into CreateColorableUtxosAsync.
        Assert.True(gates[0].SpanStart < Single(replenish, "CreateColorableUtxosAsync").SpanStart,
            "the refused-demand gate must precede CreateColorableUtxosAsync");
    }

    // P-C11. The eligibility gate, the twin of P-C10 and the one that carries the cooldown, the quarantine
    // and the wrong-wallet refusal. Nothing pinned it: narrowing the condition to
    // `if (skip.HasValue && skip.Value != ReplenishOutcome.SkipCooldown)` compiles without a warning and
    // leaves all ten other pins green — it is not a BinaryExpression with an `Outcome` left operand, so
    // P-C10's filter still finds exactly one gate — while making SkipCooldown stop nothing, so an eligible
    // wallet signs every sweep instead of every cooldown. The same edit disposes of SkipQuarantined and
    // SkipWalletNotConfigured. EvaluateReplenishEligibility is unit-tested for its RETURN VALUE; only this
    // pin tests that the caller acts on it.
    [Fact]
    public void PC11_EligibilityRefusalIsUnconditionalAndLeavesTheIteration()
    {
        var replenish = ReplenishMethod(PluginCompilation.Shared);

        // `skip.HasValue`, `skip is not null` and `skip is { }` are the same test; all three are accepted, so
        // a maintainer tidying the null-check does not hit a false failure and reach for the one repair that
        // would reinstate the hole — widening this filter to "any `if` mentioning skip", which would let a
        // narrowed condition back in.
        bool TestsSkipForPresence(ExpressionSyntax c) => c switch
        {
            MemberAccessExpressionSyntax m =>
                m.Name.Identifier.ValueText == "HasValue" && Names(m.Expression, "skip"),
            IsPatternExpressionSyntax p => Names(p.Expression, "skip") && p.Pattern is
                UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax } }
                or RecursivePatternSyntax { PropertyPatternClause.Subpatterns.Count: 0 },
            _ => false
        };

        var gates = replenish.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => TestsSkipForPresence(i.Condition))
            .ToList();
        Assert.True(gates.Count == 1,
            $"expected exactly one gate testing `skip` for presence and nothing else, found {gates.Count} — "
            + "a condition that also excludes particular outcomes is the round-10 false-ACCEPT");

        AssertLeavesTheIterationUnconditionally(gates[0], "the eligibility gate");
    }

    static bool Names(ExpressionSyntax e, string identifier) =>
        e is IdentifierNameSyntax id && id.Identifier.ValueText == identifier;

    /// <summary>
    /// The gate's block must `continue` as a direct statement, not inside a nested condition. A `continue`
    /// buried under another `if` lets the outcomes that condition excludes fall through to the signing call.
    /// </summary>
    static void AssertLeavesTheIterationUnconditionally(IfStatementSyntax gate, string what)
    {
        var block = Assert.IsType<BlockSyntax>(gate.Statement);
        Assert.True(block.Statements.OfType<ContinueStatementSyntax>().Any(),
            $"{what} must end in an unconditional `continue`; found "
            + $"[{string.Join("; ", block.Statements.Select(st => st.Kind().ToString()))}]");
    }
}
