using System.Threading.Channels;
using NBitcoin;
using WalletWasabi.Crypto;
using WalletWasabi.WabiSabi.Models;

public class XCoin:IEquatable<XCoin>
{
    public string Id { get; set; }
    public decimal Value { get; set; }
    public decimal PrivacyScore { get; set; }

    public Task<OwnershipProof> GetOwnershipProof()
    {
        return Task.FromResult(new OwnershipProof());
    }

    public bool Equals(XCoin? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id;
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((XCoin) obj);
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }
}

public enum Phase
{
    InputRegistration,
    ConnectionConfirmation,
    OutputRegistration,
    TransactionSigning,
    Ended
}

public abstract class BaseBehaviorTrait : IDisposable
{
    protected CoinjoinClient Client { get; set; }

    public virtual void StartCoinjoin(CoinjoinClient client)
    {
        Client = client;
        Client.PhaseChanged += OnPhaseChanged;
    }

    protected virtual void OnPhaseChanged(object? sender, Phase phase){}

    public virtual void Dispose()
    {
        Client.PhaseChanged -= OnPhaseChanged;
    }
}


public class CoinjoinException:Exception
{
    public CoinjoinException(string message) : base(message)
    {
    }
}

public class CoinjoinClient : IDisposable
{
    public List<XCoin> AvailableCoins { get; }
    public List<XCoin> RemainingfAvailableCoins  => AvailableCoins.Except(AllocatedSelectedCoins.Keys).ToList();
    public Dictionary<XCoin, BaseBehaviorTrait?> AllocatedSelectedCoins { get; set; }
    public List<XCoin> RegisteredCoins { get; set; }
    public List<BaseBehaviorTrait> BehaviorTraits { get; }
    public RoundState? CurrentRoundState { get; set; }
    public event EventHandler<Phase>? PhaseChanged;
    public event EventHandler? StartCoinSelection;
    public event EventHandler<Dictionary<XCoin, BaseBehaviorTrait>>? FinishedCoinSelection;
    public event EventHandler<List<XCoin>>? FinishedCoinRegistration;

    private readonly Channel<Phase> _phaseChannel = Channel.CreateBounded<Phase>(Enum.GetValues<Phase>().Length);
    
    public int MaxCoinRegistration { get;} = Random.Shared.Next(20, 31);
    

    public CoinjoinClient(RoundState roundState, List<XCoin> availableCoins, List<BaseBehaviorTrait> behaviorTraits)
    {
        AvailableCoins = availableCoins;
        BehaviorTraits = behaviorTraits;

        foreach (var behaviorTrait in behaviorTraits)
        {
            behaviorTrait.StartCoinjoin(this);
        }

        PhaseChanged += OnPhaseChanged;
        OnNewRoundState(roundState);
        _phasesTask = ProcessPhases();
    }
    
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _phasesTask;

    private async Task ProcessPhases( )
    {
        while (!_cts.IsCancellationRequested )
        {
           await  _phaseChannel.Reader.WaitToReadAsync(_cts.Token);
           var phase = await _phaseChannel.Reader.ReadAsync(_cts.Token);
           
           if(CurrentRoundState?.Phase != phase)
               throw new CoinjoinException("Phase out of sync");
           
           switch (phase)
           {
               case Phase.InputRegistration:
                   StartCoinSelection?.Invoke(this, EventArgs.Empty);
                   FinishedCoinSelection?.Invoke(this, AllocatedSelectedCoins);

                   if (AllocatedSelectedCoins.Count > MaxCoinRegistration)
                   {
                       throw new CoinjoinException("Too many coins were allocated");
                   }

                   await RegisterCoins();
                   FinishedCoinRegistration?.Invoke(this, RegisteredCoins);
                   break;
               case Phase.ConnectionConfirmation:
                   break;
               case Phase.OutputRegistration:
                   break;
               case Phase.TransactionSigning:
                   break;
               case Phase.Ended:
                   break;
           }
           
        }
    }

    private async Task RegisterCoins()
    {
        
    }

    private void OnPhaseChanged(object? sender, Phase e)
    {
        _phaseChannel.Writer.TryWrite(e);
    }

    public void OnNewRoundState(RoundState newRoundState)
    {
        var oldPhase = CurrentRoundState?.Phase;
        CurrentRoundState = newRoundState;
        if (oldPhase != newRoundState.Phase)
            PhaseChanged?.Invoke(this, newRoundState.Phase);
    }


    public void Dispose()
    {
        PhaseChanged = null;
        StartCoinSelection = null;
        FinishedCoinSelection = null;
        FinishedCoinRegistration = null;
        foreach (var behaviorTrait in BehaviorTraits)
        {
            behaviorTrait.Dispose();
        }
        if(!_cts.IsCancellationRequested)
         _cts.Cancel();
    }

    List<BaseBehaviorTrait> _failedTraits = new();
    public void TraitFailed(BaseBehaviorTrait trait)
    {
        _failedTraits.Add(trait);
        BehaviorTraits.Remove(trait);
    }
}
public class PrivacyBehavior : BaseBehaviorTrait
{
    public override void StartCoinjoin(CoinjoinClient client)
    {
        base.StartCoinjoin(client);
        client.StartCoinSelection += OnStartCoinSelection;
    }

    private void OnStartCoinSelection(object? sender, EventArgs e)
    {
        var coinsSelected = new List<XCoin>();
        //try to find a set of coins that pays a set of payment requests
        while (Client.RemainingfAvailableCoins.Any() && (Client.AllocatedSelectedCoins.Count + coinsSelected.Count) < Client.MaxCoinRegistration)
        {
            var coin = Client.RemainingfAvailableCoins.First();
            Client.AllocatedSelectedCoins.Add(coin, this);
            coinsSelected.Add(coin);
        }
        if(!coinsSelected.Any())
            Client.TraitFailed(this);
    }
}




public class PaymentRequest
{
    public decimal Amount { get; set; }
    public string Address { get; set; }
    public string? Endpoint { get; }

    public PaymentRequest(decimal amount, string address, string? endpoint = null)
    {
        Amount = amount;
        Address = address;
        Endpoint = endpoint;
    }
}




public class PaymentBehavior : BaseBehaviorTrait
{
    public PaymentRequest[] PaymentRequests { get; }
    public PaymentRequest[] ExpectedToHandle { get; }

    public PaymentBehavior(PaymentRequest[] paymentRequests)
    {
        PaymentRequests = paymentRequests;
    }

    public override void StartCoinjoin(CoinjoinClient client)
    {
        base.StartCoinjoin(client);
        
    client.StartCoinSelection += OnStartCoinSelection;    
    }

    private void OnStartCoinSelection(object? sender, EventArgs e)
    {
        var remainingPaymentRequests = PaymentRequests.ToArray();
        var handledPaymentRequests = new List<PaymentRequest>();
        List<XCoin> selectCoins = new();
        //try to find a set of coins that pays a set of payment requests
        while (remainingPaymentRequests.Any() && Client.RemainingfAvailableCoins.Any() &&
               (selectCoins.Count + Client.AllocatedSelectedCoins.Count) <= Client.MaxCoinRegistration)
        {
            //you can use multiple coins to pay for a single or multiple payment request(s)
    
            

        }
        if(!handledPaymentRequests.Any())
            Client.TraitFailed(this);
    }
}


// I need a fluent interface to create a CoinjoinClient
// Given a round id on a coordinator url, and my list of available coins, I want to be able to create a CoinjoinClient
//Additionally, I want to be able to provide (multiple) Behavior traits so that I can guide the coinjoin to proceed with the round based on the behavior traits
// Forr example, I would have a behavior trait to make my wallet private, this  would mean it looks at the coins I provide and if they are not private, it would not proceed with the coinjoin. The outputs would be sent back to own wallet (new addresses) and the client would only sign if the outputs are more private than the inputs
// Traits: PrivacyBehavior, PaymentBatchingBehavior, P2EPPaymentBehavior, ConsolidationBehavior
//PrivacyBehavior: Take inputs and make them more private if they are not private
//PaymentBatchingBehavior: If there are payments to be made, use the inputs and make outputs in the coinjoin paying the recipients
//P2EPPaymentBehavior: If there are payments to be made, and they support coinjoinp2ep, initiate a connection to them and once you register enough coins for the payment, send the credentials issued from coordinator in exchaneg for the inpputs
//ConsolidationBehavior: Register less outputs than inputs