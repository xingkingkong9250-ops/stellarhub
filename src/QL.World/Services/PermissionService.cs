using QL.World.Models;

namespace QL.World.Services;

/// <summary>
/// M6-F: central permission checks. Evaluates whether the current
/// <see cref="CurrentUser"/> (or null in local-only mode) may perform an
/// action. Callers gate UI visibility / command CanExecute on these.
/// </summary>
public sealed class PermissionService
{
    private readonly CurrentUser? _currentUser;

    public PermissionService(CurrentUser? currentUser)
    {
        _currentUser = currentUser;
    }

    /// <summary>True when someone is logged in with a NAS account.</summary>
    public bool IsLoggedIn => _currentUser is not null;

    /// <summary>True when the logged-in user is an admin.</summary>
    public bool IsAdmin => _currentUser?.IsAdmin == true;

    /// <summary>The logged-in username, or null in local-only mode.</summary>
    public string? Username => _currentUser?.Username;

    /// <summary>Admins and the original submitter can manage a submission.</summary>
    public bool CanManageSubmission(string submittedBy)
        => IsAdmin || (Username is not null && Username == submittedBy);

    /// <summary>Only admins can approve/reject submissions.</summary>
    public bool CanApproveSubmissions => IsAdmin;

    /// <summary>Only admins can manage user accounts.</summary>
    public bool CanManageUsers => IsAdmin;

    /// <summary>Any logged-in user can submit to the shared library.</summary>
    public bool CanSubmitToLibrary => IsLoggedIn;

    /// <summary>Any logged-in user can view the shared library.</summary>
    public bool CanViewSharedLibrary => IsLoggedIn;
}
