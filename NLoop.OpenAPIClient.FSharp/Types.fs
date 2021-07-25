namespace rec NLoop.OpenAPIClient.FSharp.Types

///Bitcoin public key in 33 bytes length
type PubKey = string

type LoopOutRequest =
    { ///The number of blocks from the on-chain HTLC's confirmation height that it should be swept within.
      sweep_conf_target: Option<int>
      max_miner_fee: Option<int64>
      max_swap_fee: Option<int64>
      ///Bitcoin public key in 33 bytes length
      dest: PubKey
      amount: int64 }
    ///Creates an instance of LoopOutRequest with all optional fields initialized to None. The required fields are parameters of this function
    static member Create (dest: PubKey, amount: int64): LoopOutRequest =
        { sweep_conf_target = None
          max_miner_fee = None
          max_swap_fee = None
          dest = dest
          amount = amount }

type LoopInRequest =
    { max_miner_fee: Option<int64>
      max_swap_fee: Option<int64>
      amount: int64
      channel_id: int }
    ///Creates an instance of LoopInRequest with all optional fields initialized to None. The required fields are parameters of this function
    static member Create (amount: int64, channel_id: int): LoopInRequest =
        { max_miner_fee = None
          max_swap_fee = None
          amount = amount
          channel_id = channel_id }

type LoopOutResponse =
    { ///Swap identifier to track status.
      id: Option<string>
      ///On chain address
      htlc_target: Option<BitcoinAddressNonMalleable> }
    ///Creates an instance of LoopOutResponse with all optional fields initialized to None. The required fields are parameters of this function
    static member Create (): LoopOutResponse = { id = None; htlc_target = None }

type LoopInResponse =
    { ///Swap identifier to track status.
      id: Option<string>
      ///On chain address
      htlc_address: Option<BitcoinAddressNonMalleable> }
    ///Creates an instance of LoopInResponse with all optional fields initialized to None. The required fields are parameters of this function
    static member Create (): LoopInResponse = { id = None; htlc_address = None }

[<RequireQualifiedAccess>]
type Version =
    ///OK
    OK of payload: string

[<RequireQualifiedAccess>]
type Out =
    ///OK
    OK of payload: LoopOutResponse

[<RequireQualifiedAccess>]
type In =
    ///OK
    OK of payload: LoopInResponse
