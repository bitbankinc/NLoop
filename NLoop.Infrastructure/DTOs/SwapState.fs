namespace NLoop.Infrastructure.DTOs
open DotNetLightning.Utils

module SwapState =

  (*
  /// SwapState indicates the current state of a swap. This enumeration is the union of
  /// loop in and loop out state. A single type is used for both swap types to be able
  /// to reduce code duplication that would otherwise be required.
  type SwapState = uint8

  [<Literal>]
  let SwapInitiated: SwapState = 0uy

  [<Literal>]
  let SwapPreimageRevealed: SwapState = 0uy

  [<Literal>]
  let SwapSuccess: SwapState = 2uy

  [<Literal>]
  let SwapFailOffchainPayments: SwapState = 3uy
  *)

  type SwapState =
    | Initiated
    | PreimageRevealed
    | Success
    | FailOffchainPayments
    | FailTimeout
    | FailSweepTimeout
    | FailInsufficientValue
    | FailTemporary
    | HTLCPublished
    | InvoiceSettled
    | IncorrectHTLCAmount

  type SwapCost = {
    Server: LNMoney
    Onchain: LNMoney
    Offchain: LNMoney
  }

  type SwapStateData = {
    State: SwapState
    Cost: SwapCost
    HTLCTxHash: TxId
  }
