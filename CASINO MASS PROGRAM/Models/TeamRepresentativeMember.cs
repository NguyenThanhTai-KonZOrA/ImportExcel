namespace CASINO_MASS_PROGRAM.Models;

public class TeamRepresentativeMember
{
    public Guid TeamRepresentativeId { get; set; }
    public TeamRepresentative? TeamRepresentative { get; set; }

    public Guid MemberId { get; set; }
    public Member? Member { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}