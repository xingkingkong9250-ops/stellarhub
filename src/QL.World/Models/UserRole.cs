namespace QL.World.Models;

/// <summary>
/// M6-A: application-level user role. Stored as a string in accounts.json
/// so the JSON stays human-readable and protocol-agnostic.
/// </summary>
public enum UserRole
{
    /// <summary>Full control: manage users, approve submissions, edit shared library.</summary>
    Admin,

    /// <summary>Read shared library, submit images for review, manage own local library.</summary>
    User
}
