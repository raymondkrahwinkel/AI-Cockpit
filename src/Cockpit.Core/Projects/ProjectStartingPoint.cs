namespace Cockpit.Core.Projects;

// AC-488: a set of answers to the project the operator would otherwise fill in by hand — not a second concept
// beside `Project` (AC-487, 2026-07-21), since choosing one leaves a plain project behind. `Needs` names what the
// folder should hold ("a folder of invoices"): shown on the card, and the title of the one dialog a card opens.
public sealed record ProjectStartingPoint(
    string Name,
    string Description,
    string Needs,
    string Behavior,
    IReadOnlyList<ProjectJob> Jobs)
{
    // The promise every starting point's sessions are held to, ahead of their own behaviour. It is the whole
    // reason this audience can be handed an assistant at all: nothing leaves the machine and nothing is booked
    // without a person pressing the button.
    public const string SharedBehavior =
        "Never send, submit, book or pay anything. You prepare, compare, explain and put things ready for review " +
        "— a person decides and a person sends. Never change a source file; write your results into the project's " +
        "own folder. When you cannot match something, stop and list it rather than guess.";

    // Where a starting point writes, relative to the chosen folder. Everything a session produces lands under one
    // subfolder of the source, which is what keeps the folder the only question: without it every card would have
    // to ask a second one ("where may I write?").
    public const string OutputFolderName = "Cockpit";

    // A plain project on `folder`, filled from this starting point. Nothing here is a new field or a new shape —
    // the same values the project editor would have written had the operator typed them in.
    public Project Create(string folder) => Project.Create(Name) with
    {
        Description = Description,
        SourceDirectories = [new ProjectRepository(folder)],
        MemoryRef = Path.Combine(folder, OutputFolderName, "memory"),
        BehaviorPrompt = SharedBehavior + "\n\n" + Behavior,
        Jobs = Jobs,

        // Nothing ticked, deliberately: there is no bookkeeping, mail or spreadsheet server to tick, and the
        // developer tooling in the registry is not what this project is for. `DefaultProfileLabel` stays null for
        // the same honesty — no template knows this machine's labels, so the card asks once, like any new project.
        McpOverlay = new ProjectMcpOverlay { EnabledServerNames = [] },
    };

    // The five, in the order the gallery shows them. Their content is Raymond's, recorded on AC-488 (criterion 5):
    // "Sales follow-up" is absent because it hangs on a mailbox nothing here can reach — it waits on a connection
    // rather than having been dropped — and the jobs that would have guessed at a file format are absent too.
    public static IReadOnlyList<ProjectStartingPoint> All { get; } =
    [
        new ProjectStartingPoint(
            "Invoices & bookkeeping",
            "Read incoming invoices, pull out the figures, and prepare them for the books.",
            "a folder of invoices",
            "You work with invoices as files. Always report per invoice which figures you read and where you read "
                + "them, so a person can check you without opening the document.",
            [
                new ProjectJob(
                    "Read this month's invoices and put supplier, date, number, amount and VAT in one overview.",
                    "writes one overview · changes no invoice"),
                new ProjectJob(
                    "Compare this month against the earlier months and flag anything that looks booked twice.",
                    "changes nothing · reports only"),
                new ProjectJob(
                    "List the suppliers whose invoice has not arrived this month, and draft a reminder for each.",
                    "drafts emails · you send them"),
            ]),
        new ProjectStartingPoint(
            "Expenses & receipts",
            "Sort receipts and expense claims, and get them ready for the bookkeeping.",
            "a folder of receipts",
            "Report per receipt what you could and could not read from it. A receipt you cannot read goes on the "
                + "list of things to check, never on the overview as a guess.",
            [
                new ProjectJob(
                    "Read this month's receipts, pull out date, supplier, amount and VAT, and sort them per month.",
                    "writes one overview · changes no receipt"),
                new ProjectJob(
                    "List the receipts that are too poor, incomplete or ambiguous to process.",
                    "changes nothing · reports only"),
                new ProjectJob(
                    "Line up the receipts with the claims that were submitted and list what is missing on either side.",
                    "changes nothing · reports only"),
                new ProjectJob(
                    "Prepare the batch for the bookkeeper: one overview plus the receipts that belong with it.",
                    "writes one file · nothing is sent"),
            ]),
        new ProjectStartingPoint(
            "Reporting",
            "Pull numbers together from sheets and exports into a report you can check.",
            "a folder with your spreadsheets",
            "Say for every figure which file and which sheet it came from. Do the sums with plain arithmetic and "
                + "show them, so a person can redo them.",
            [
                new ProjectJob(
                    "Read the sheets and exports in the folder and build one overview per month.",
                    "writes one overview · changes no sheet"),
                new ProjectJob(
                    "Compare this period with the previous one and list the biggest differences, and what causes each.",
                    "changes nothing · reports only"),
                new ProjectJob(
                    "Check the sheets before they go out: totals, missing periods, doubled rows, signs the wrong way round.",
                    "changes nothing · reports only"),
                new ProjectJob(
                    "Draft the short accompanying note for whoever receives the report.",
                    "writes one draft · nothing is sent"),
            ]),
        new ProjectStartingPoint(
            "Contracts & documents",
            "Read long documents, find the clauses that matter, compare versions.",
            "a folder of documents",
            "Quote the passage you base an answer on, with the document name. If a document does not say "
                + "something, say that it does not say it rather than filling the gap.",
            [
                new ProjectJob(
                    "Summarise a document: what it says, per party, on one page.",
                    "changes nothing · reports only"),
                new ProjectJob(
                    "Find the clauses that matter: term, notice period, price indexation, liability, renewal.",
                    "changes nothing · reports only"),
                new ProjectJob(
                    "Compare two versions, list every difference, and say which side each one favours.",
                    "changes nothing · reports only"),
                new ProjectJob(
                    "Go through the folder and list the contracts with a deadline in the next three months.",
                    "writes one list · nothing is sent"),
            ]),
        new ProjectStartingPoint(
            "Meetings & actions",
            "Turn recordings or notes into a summary and a list of who does what.",
            "a folder of notes",
            "An action always names a person and a date. If either is missing from the notes, put the action on "
                + "the list marked as unclear rather than inventing one.",
            [
                new ProjectJob(
                    "Turn these notes into a summary and an action list: who does what, by when.",
                    "writes one file · nothing is sent"),
                new ProjectJob(
                    "Go through the folder and list the actions from earlier meetings that were never marked done.",
                    "changes nothing · reports only"),
                new ProjectJob(
                    "Draft the follow-up message: the summary, as a message to the people who were there.",
                    "drafts an email · you send it"),
                new ProjectJob(
                    "Prepare the next agenda, from what is still open plus the last set of notes.",
                    "writes one file · nothing is sent"),
            ]),
    ];
}
