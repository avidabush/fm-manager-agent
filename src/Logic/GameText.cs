namespace ManagerAgent;

/// <summary>
/// The on-screen words the agent looks for. Defaults are the English game texts; each can be changed
/// in the config ([Game text]) for other languages. "|" separates alternatives.
/// </summary>
internal sealed class GameText
{
    // Jobs page
    public string AvailableJobs = "Available Jobs";
    public string ApplyForJob = "Apply For Job";
    public string ApplyForAllJobs = "Apply for all jobs";
    public string ConfirmTitle = "Please Confirm";
    public string Yes = "Yes|Apply|Confirm|OK";
    public string No = "No|Cancel";
    public string Vacancy = "Available";
    public string ColCoach = "Current Head Coach|Current Manager";
    public string ColTeam = "Team";
    public string ColRating = "Club Rating";
    public string ColNation = "Nation";
    public string ColPosition = "Position";
    public string ColSecurity = "Job Security";
    public string ColFinances = "Finances";
    public string Ratings = "Local|Regional|National|Continental|Worldwide";
    public string SecurityWords = "Secure|Very Secure|Insecure|Very Insecure|Unstable|Stable|Under Threat|Safe|Vulnerable";
    public string FinanceWords = "Okay|Rich|Very Rich|Secure|Insecure|Poor|Comfortable|Stable|Healthy|Precarious|In Debt|Good|Excellent|Bad|Struggling";
    // Navigation
    public string CareerMenu = "Career";
    public string JobsMenuItems = "Head Coach Vacancies|Manager Vacancies|Available Jobs|Job Vacancies|Vacancies|Job Centre|Jobs";
    // Offer message
    public string EnterNegotiations = "Enter Contract Negotiations";
    public string RejectOffer = "Reject Offer";
    // Contract screen
    public string CurrentOffer = "Current Offer";
    public string JobRow = "Job";
    public string Salary = "Salary";
    public string ContractLength = "Contract Length";
    public string AddClause = "Add Clause";
    public string ClausesHeader = "Bonuses & Clauses|Bonuses and Clauses|Clauses";
    public string SuggestTerms = "Suggest Terms";
    public string FinalizeDeal = "Finalize Deal|Finalise Deal";
    public string AboutClub = "About Club";
    public string ResetButton = "Reset";
}
