using DoraDesk.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace DoraDesk.Web.Components;

/// <summary>Base for interactive pages: the signed-in actor, and business errors shown instead of crashing the circuit.</summary>
public abstract class DeskPage : ComponentBase
{
    [CascadingParameter] Task<AuthenticationState>? AuthState { get; set; }
    [Inject] protected IncidentService Incidents { get; set; } = default!;
    [Inject] protected Clock Clock { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected Actor? Me { get; private set; }
    protected string? Error { get; set; }
    protected string? Notice { get; set; }
    protected bool Busy { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        if (AuthState is not null)
        {
            var state = await AuthState;
            if (state.User.Identity?.IsAuthenticated == true) Me = Actor.From(state.User);
        }
        await LoadAsync();
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Runs an action; a DeskException becomes a message for the user, anything else stays an error.</summary>
    protected async Task Run(Func<Actor, Task> action, string? success = null)
    {
        if (Busy) return;
        Error = null;
        Notice = null;
        if (Me is null) { Error = "Please sign in again."; return; }
        Busy = true;
        try
        {
            await action(Me);
            Notice = success;
        }
        catch (DeskException e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
