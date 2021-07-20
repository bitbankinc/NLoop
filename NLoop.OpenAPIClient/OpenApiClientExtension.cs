#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using NLoop.Domain;

namespace NLoopClient
{
  public partial class NLoopClient
  {
    HubConnection? _connection;
    public IAsyncEnumerable<Swap.Event> ListenToEventsAsync(CancellationToken cancellationToken = default)
    {
      var urlBuilder = new System.Text.StringBuilder();
      urlBuilder.Append(BaseUrl != null ? BaseUrl.TrimEnd('/') : "").Append("/v1/events");
      var uri = new Uri(urlBuilder.ToString(), UriKind.RelativeOrAbsolute);

      _connection =
        new HubConnectionBuilder()
          .WithUrl(uri)
          .WithAutomaticReconnect()
          .Build();
      var s = _connection.StreamAsync<Swap.Event>("ListenSwapEvents", cancellationToken);
      return s;
    }
  }
}
