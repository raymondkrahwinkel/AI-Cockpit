namespace Cockpit.Core.Wireframe.Model;

// Every member's lowercase name is its keyword in the source text (AC-871), so the parser and the writer share one
// vocabulary instead of a keyword table that can drift from the enum.
public enum WireframeNodeKind
{
    Screen,
    // AC-914: a screen's variant, replacing one of its containers' content — kept beside Screen, the other kind
    // that stands for a whole screen rather than something drawn inside one.
    State,
    Row,
    Column,
    Group,
    Header,
    Footer,
    Sidebar,
    Main,
    Card,
    Modal,
    Tabs,
    Tab,
    Nav,
    Menu,
    Breadcrumb,
    Stepper,
    List,
    Table,

    Item,
    Label,
    Button,
    Input,
    Textarea,
    Search,
    Select,
    Checkbox,
    Radio,
    Toggle,
    Slider,
    Image,
    Avatar,
    Icon,
    Badge,
    Progress,
    Pagination,
    Divider,
    Space,
}
