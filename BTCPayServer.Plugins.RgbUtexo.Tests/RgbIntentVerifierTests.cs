using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbIntentVerifierTests
{
    static readonly Network Net = Network.RegTest;
    const string ContractId = "rgb:2WBcas9-yA7soVimc-C1SQ34ry8-adfawij2j-asd23f8sd-abcdef";
    const string RecipientSeal = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";

    class FakeChainClient : IBitcoinChainClient
    {
        public Func<string, string>? RawTx;
        public Func<Script, IReadOnlyList<Outpoint>>? Unspent;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetRawTransactionAsync(string txid, CancellationToken ct = default)
            => Task.FromResult(RawTx?.Invoke(txid) ?? throw new InvalidOperationException("no raw tx"));
        public Task<string> BroadcastTransactionAsync(string rawTxHex, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<Outpoint>> ListUnspentByScriptAsync(Script script, CancellationToken ct = default)
            => Task.FromResult(Unspent?.Invoke(script) ?? (IReadOnlyList<Outpoint>)Array.Empty<Outpoint>());
        public void Dispose() { }
    }

    class Ctx
    {
        public required MemoryWalletSigner Signer;
        public required PSBT Psbt;
        public required string UnsignedTxid;
        public required Script ChangeScript;
        public required RgbDecodeInvoiceResult Decode;
        public required RgbValidateResult Validate;
        public required RgbCommitmentCheckResult Commitment;
        public required List<string> Staged;
        public long OperatorAmount = 100;
        public string OperatorAssetId = ContractId;
        public FakeChainClient Chain = new();

        public Task Run() => RgbIntentVerifier.VerifyAsync(
            Decode, Validate, Commitment, Psbt, UnsignedTxid, Signer, Net, OperatorAmount, OperatorAssetId, Staged, Chain);
    }

    static Ctx Valid()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var signer = new MemoryWalletSigner(mnemonic, Net);
        var colored = ExtPubKey.Parse(signer.XpubColored, Net);
        var changeScript = colored.Derive(0).Derive(0).PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;

        var opret = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(Convert.FromHexString(RecipientSeal)));
        var tx = Net.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.Parse("1111111111111111111111111111111111111111111111111111111111111111"), 3));
        tx.Outputs.Add(new TxOut(Money.Zero, opret));
        tx.Outputs.Add(new TxOut(Money.Coins(1), changeScript));
        var psbt = tx.CreatePSBT(Net);
        var unsignedTxid = psbt.GetGlobalTransaction().GetHash().ToString();
        var prevout = $"{psbt.GetGlobalTransaction().Inputs[0].PrevOut.Hash}:{psbt.GetGlobalTransaction().Inputs[0].PrevOut.N}";

        return new Ctx
        {
            Signer = signer,
            Psbt = psbt,
            UnsignedTxid = unsignedTxid,
            ChangeScript = changeScript,
            Decode = new RgbDecodeInvoiceResult
            {
                ContractId = ContractId,
                AmountKind = "amount",
                Amount = 100,
                RecipientSeal = RecipientSeal,
                RecipientChainNet = "bcrt",
                Expiry = null,
                Transports = ["rpc://proxy.example/0.2/json-rpc"]
            },
            Validate = new RgbValidateResult
            {
                ContractId = ContractId,
                ChainNet = "bcrt",
                WitnessTxid = unsignedTxid,
                Prevouts = [prevout],
                Legs =
                [
                    new RgbLeg { AssignmentType = 4000, SealKind = "confidentialSeal", SealBytes = RecipientSeal, Amount = 100 },
                    new RgbLeg { AssignmentType = 4000, SealKind = "revealedWitnessVout", WitnessVout = 1, Amount = 900 }
                ]
            },
            Commitment = new RgbCommitmentCheckResult
            {
                Matches = true,
                WitnessIdMatches = true,
                CommittedContractIds = [ContractId]
            },
            Staged = ["rpc://proxy.example/0.2/json-rpc"]
        };
    }

    [Fact]
    public async Task ValidTransfer_Passes()
    {
        await Valid().Run();
    }

    [Fact]
    public async Task WrongAsset_Rejected()
    {
        var c = Valid();
        c.Validate.ContractId = "rgb:different-contract-id";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task OperatorApprovedDifferentAsset_Rejected()
    {
        var c = Valid();
        c.OperatorAssetId = "rgb:some-other-asset-the-operator-picked";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task OperatorApprovedEmptyAsset_Rejected()
    {
        var c = Valid();
        c.OperatorAssetId = "";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task OperatorAssetId_RgbPrefixTolerant_Passes()
    {
        var c = Valid();
        c.OperatorAssetId = ContractId.Substring(4);
        await c.Run();
    }

    [Fact]
    public async Task OperatorApprovedDifferentAmount_EmbeddedInvoice_Rejected()
    {
        var c = Valid();
        c.OperatorAmount = 50;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task WrongWitnessTxid_Rejected()
    {
        var c = Valid();
        c.Validate.WitnessTxid = "0000000000000000000000000000000000000000000000000000000000000000";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task WrongPrevout_Rejected()
    {
        var c = Valid();
        c.Validate.Prevouts = ["deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef0:0"];
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task WrongRecipientSeal_Rejected()
    {
        var c = Valid();
        c.Validate.Legs[0].SealBytes = "00" + RecipientSeal[2..];
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task WrongRecipientAmount_Rejected()
    {
        var c = Valid();
        c.Validate.Legs[0].Amount = 99;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task RecipientAssignmentTypeNotAsset_Rejected()
    {
        var c = Valid();
        c.Validate.Legs[0].AssignmentType = 9999;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task NonOwnChangeLeg_Rejected()
    {
        var c = Valid();
        var foreign = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;
        var tx = c.Psbt.GetGlobalTransaction();
        tx.Outputs[1].ScriptPubKey = foreign;
        c.Psbt = tx.CreatePSBT(Net);
        c.UnsignedTxid = c.Psbt.GetGlobalTransaction().GetHash().ToString();
        c.Validate.WitnessTxid = c.UnsignedTxid;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ConcealedNonRecipientLeg_Rejected()
    {
        var c = Valid();
        c.Validate.Legs.Add(new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "confidentialSeal",
            SealBytes = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            Amount = 5
        });
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task WrongConsignmentNetwork_Rejected()
    {
        var c = Valid();
        c.Validate.ChainNet = "bc";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task UnmappedConsignmentNetwork_FailClosed()
    {
        var c = Valid();
        c.Validate.ChainNet = "tb4";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task RecipientNetworkMismatch_Rejected()
    {
        var c = Valid();
        c.Decode.RecipientChainNet = "bc";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task NonPlainTaprootOutput_Rejected()
    {
        var c = Valid();
        var foreign = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;
        var tx = c.Psbt.GetGlobalTransaction();
        tx.Outputs.Add(new TxOut(Money.Coins(1), foreign));
        c.Psbt = tx.CreatePSBT(Net);
        c.UnsignedTxid = c.Psbt.GetGlobalTransaction().GetHash().ToString();
        c.Validate.WitnessTxid = c.UnsignedTxid;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task EndpointMismatch_Rejected()
    {
        var c = Valid();
        c.Staged = ["rpc://attacker.example/0.2/json-rpc"];
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task EndpointSchemeTranslated_RpcToHttp_Passes()
    {
        var c = Valid();
        c.Decode.Transports = ["rpc://proxy.example/0.2/json-rpc"];
        c.Staged = ["http://proxy.example/0.2/json-rpc"];
        await c.Run();
    }

    [Fact]
    public async Task EndpointSchemeTranslated_RpcsToHttps_Passes()
    {
        var c = Valid();
        c.Decode.Transports = ["rpcs://proxy.iriswallet.com/0.2/json-rpc"];
        c.Staged = ["https://proxy.iriswallet.com/0.2/json-rpc"];
        await c.Run();
    }

    [Fact]
    public async Task EndpointSchemeTranslated_DifferentHost_Rejected()
    {
        var c = Valid();
        c.Decode.Transports = ["rpc://proxy.example/0.2/json-rpc"];
        c.Staged = ["http://attacker.example/0.2/json-rpc"];
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task CommitmentNotMatching_Rejected()
    {
        var c = Valid();
        c.Commitment.Matches = false;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task CommitmentWitnessMismatch_Rejected()
    {
        var c = Valid();
        c.Commitment.WitnessIdMatches = false;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task CommittedContractSetWrong_Rejected()
    {
        var c = Valid();
        c.Commitment.CommittedContractIds = [ContractId, "rgb:extra-contract"];
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ExpiredInvoice_Rejected()
    {
        var c = Valid();
        c.Decode.Expiry = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ConcreteOutpointChange_OwnedAndUnspent_Passes()
    {
        var c = Valid();
        var fundingTx = Net.CreateTransaction();
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0));
        fundingTx.Outputs.Add(new TxOut(Money.Coins(1), c.ChangeScript));
        var fundingTxid = fundingTx.GetHash().ToString();
        c.Chain.RawTx = _ => fundingTx.ToHex();
        c.Chain.Unspent = _ => new List<Outpoint> { new(fundingTxid, 0) };
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = $"{fundingTxid}:0",
            Amount = 900
        };
        await c.Run();
    }

    [Fact]
    public async Task DuplicateRecipientLeg_Rejected()
    {
        var c = Valid();
        c.Validate.Legs.Add(new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "confidentialSeal",
            SealBytes = RecipientSeal,
            Amount = 100
        });
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task UnknownChangeSealKind_FailClosed()
    {
        var c = Valid();
        c.Validate.Legs[1].SealKind = "tapretFirst";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ConcreteOutpointIsPsbtInput_Rejected()
    {
        var c = Valid();
        var inputPrevout = $"{c.Psbt.GetGlobalTransaction().Inputs[0].PrevOut.Hash}:{c.Psbt.GetGlobalTransaction().Inputs[0].PrevOut.N}";
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = inputPrevout,
            Amount = 900
        };
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ConcreteOutpointNonOwnScript_Rejected()
    {
        var c = Valid();
        var foreign = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;
        var fundingTx = Net.CreateTransaction();
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0));
        fundingTx.Outputs.Add(new TxOut(Money.Coins(1), foreign));
        var fundingTxid = fundingTx.GetHash().ToString();
        c.Chain.RawTx = _ => fundingTx.ToHex();
        c.Chain.Unspent = _ => new List<Outpoint> { new(fundingTxid, 0) };
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = $"{fundingTxid}:0",
            Amount = 900
        };
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task UnexpectedAmountKind_FailClosed()
    {
        var c = Valid();
        c.Decode.AmountKind = "bogus";
        c.Decode.Amount = null;
        c.OperatorAmount = 100;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task OperatorTypedAmount_WhenInvoiceOmitsAmount_Passes()
    {
        var c = Valid();
        c.Decode.AmountKind = "absent";
        c.Decode.Amount = null;
        c.OperatorAmount = 100;
        await c.Run();
    }

    [Fact]
    public async Task OperatorTypedAmountMismatch_Rejected()
    {
        var c = Valid();
        c.Decode.AmountKind = "absent";
        c.Decode.Amount = null;
        c.OperatorAmount = 50;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ConcreteOutpointChange_Spent_Rejected()
    {
        var c = Valid();
        var fundingTx = Net.CreateTransaction();
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0));
        fundingTx.Outputs.Add(new TxOut(Money.Coins(1), c.ChangeScript));
        var fundingTxid = fundingTx.GetHash().ToString();
        c.Chain.RawTx = _ => fundingTx.ToHex();
        c.Chain.Unspent = _ => Array.Empty<Outpoint>();
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = $"{fundingTxid}:0",
            Amount = 900
        };
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }
}
