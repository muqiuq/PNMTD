using PNMTD.Api.V1.Contracts;

namespace PNMTD.Responsibilities;

/// <summary>
/// A responsibility's escalation order (docs/responsibilities-mcp-and-map-plan.md §3): whom to ask, one after another, as one simple
/// queue. The responsible, the deputy, the global responsible, the global deputy; whoever isn't set or is inactive is skipped and
/// the next moves up, and a person counts once, at their first place. Information only: nothing is sent to anyone, and Cookie's
/// escalations (<c>Duties</c>) don't read it. <see cref="ResponsibilityQueries.StandsIn"/> exactly when none of its own people is in it.
/// </summary>
public static class EscalationOrder
{
    public static IReadOnlyList<ResponsibilityStep> Of(ResponsibilityPerson? responsible, ResponsibilityPerson? deputy, ResponsibilityGlobal global) =>
        Of(responsible, deputy, global.Responsible, global.Deputy);

    public static IReadOnlyList<ResponsibilityStep> Of(ResponsibilityPerson? responsible, ResponsibilityPerson? deputy,
        ResponsibilityPerson? globalResponsible, ResponsibilityPerson? globalDeputy)
    {
        var steps = new List<ResponsibilityStep>(4);
        void Add(ResponsibilityPerson? person, ResponsibilityRole role)
        {
            if (person is { Active: true } && steps.TrueForAll(s => s.Person.Id != person.Id))
            {
                steps.Add(new ResponsibilityStep(steps.Count + 1, role, person));
            }
        }
        Add(responsible, ResponsibilityRole.Responsible);
        Add(deputy, ResponsibilityRole.Deputy);
        Add(globalResponsible, ResponsibilityRole.GlobalResponsible);
        Add(globalDeputy, ResponsibilityRole.GlobalDeputy);
        return steps;
    }
}
