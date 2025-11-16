using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace WebHost.Plugins
{
	// Minimal dynamic endpoint data source. Update replaces the endpoint list and
	// triggers change token so the routing system and Swagger pick up the changes.
	public class DynamicEndpointDataSource : EndpointDataSource
	{
		private IList<Endpoint> _endpoints = Array.Empty<Endpoint>();
		private CancellationChangeToken _changeToken = new CancellationChangeToken(new CancellationToken(true));
		private CancellationTokenSource _cts = new CancellationTokenSource();

		public override IReadOnlyList<Endpoint> Endpoints => _endpoints as IReadOnlyList<Endpoint>;

		public override IChangeToken GetChangeToken() => _changeToken;

		public void Update(IEnumerable<Endpoint> endpoints)
		{
			if (endpoints == null) endpoints = Array.Empty<Endpoint>();
			_endpoints = endpoints.ToArray();
			// rotate change token
			try { _cts.Cancel(); } catch { }
			_cts = new CancellationTokenSource();
			_changeToken = new CancellationChangeToken(_cts.Token);
		}
	}
}
