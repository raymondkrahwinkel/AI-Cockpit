namespace Cockpit.Plugin.Diagram.Wireframe.Model;

// Every member's lowercase name is its keyword in the source text (AC-871), so the parser and the writer share one
// vocabulary instead of a keyword table that can drift from the enum.
internal enum WireframeNodeKind
{
    Screen,
    Row,
    Column,
    Group,
    Tabs,
    Tab,
    Nav,
    List,
    Table,

    Item,
    Label,
    Button,
    Input,
    Checkbox,
    Radio,
    Select,
    Image,
    Divider,
    Space,
}
