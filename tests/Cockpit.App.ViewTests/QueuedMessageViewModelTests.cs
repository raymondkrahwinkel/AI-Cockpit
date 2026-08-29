using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

public sealed class QueuedMessageViewModelTests
{
    [Fact]
    public void DisplayText_FlattensWhitespace_WithoutChangingText()
    {
        const string text = "first\r\nsecond\nthird    fourth";
        var message = new QueuedMessageViewModel(text, [], null, _ => { });

        Assert.Equal(text, message.Text);
        Assert.Equal("first second third fourth", message.DisplayText);
        Assert.DoesNotContain('\n', message.DisplayText);
        Assert.DoesNotContain('\r', message.DisplayText);
    }
}
