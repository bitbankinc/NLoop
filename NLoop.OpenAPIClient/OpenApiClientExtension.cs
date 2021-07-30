#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using NBitcoin;
using NLoop.Domain;
using NLoop.Domain.IO;
using NLoop.Server.Actors;

namespace NLoopClient
{
  public partial class NLoopClient
  {
    HubConnection? connection;
    public async IAsyncEnumerable<SwapEventWithId> ListenToEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
      var sb = new System.Text.StringBuilder();
      sb.Append(BaseUrl != null ? BaseUrl.TrimEnd('/') : "").Append("/v1/events");
      var uri = new Uri(sb.ToString(), UriKind.RelativeOrAbsolute);

      connection =
        new HubConnectionBuilder()
          .WithUrl(uri)
          .WithAutomaticReconnect()
          .AddJsonProtocol(p =>
          {
            p.PayloadSerializerOptions.AddNLoopJsonConverters(FSharpOption<Network>.None);
          })
          .Build();
      await connection.StartAsync(cancellationToken);
      var s = connection.StreamAsync<SwapEventWithId>("ListenSwapEvents", cancellationToken);
      await foreach (var e in s.WithCancellation(cancellationToken))
      {
        yield return e;
      }
    }
  }
}
