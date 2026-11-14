using Jellyfin.Plugin.FreeStuffCrazy.Relay;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FreeStuffCrazy.Api;

/// <summary>
/// Backs the plugin's dashboard page. Administrators only: the pairing link
/// lets whoever opens it connect this server to FreeStuffCrazy Player.
/// </summary>
[ApiController]
[Route("FreeStuffCrazy")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ConnectorController : ControllerBase
{
    private readonly ConnectorState _state;

    public ConnectorController(ConnectorState state) => _state = state;

    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        return new
        {
            enabled = config?.Enabled ?? false,
            linked = !string.IsNullOrEmpty(config?.ConnectorId),
            status = _state.Status.ToString(),
            message = _state.Message,
            pairingUrl = _state.PairingUrl,
            pairingCode = _state.PairingCode,
            pairingExpiresAt = _state.PairingExpiresAt,
            label = _state.Label,
            connectedSince = _state.ConnectedSince,
            activeStreams = _state.ActiveStreams,
        };
    }

    /// <summary>Turns the connection on or off.</summary>
    [HttpPost("Enabled")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult SetEnabled([FromQuery] bool enabled)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        plugin.Configuration.Enabled = enabled;
        plugin.SaveConfiguration();
        _state.RequestRestart();
        return NoContent();
    }

    /// <summary>Forgets this server's link; it reconnects unlinked and shows a new pairing link.</summary>
    [HttpPost("Unlink")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Unlink()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        plugin.Configuration.ConnectorId = string.Empty;
        plugin.Configuration.Secret = string.Empty;
        plugin.SaveConfiguration();
        _state.RequestRestart();
        return NoContent();
    }
}
