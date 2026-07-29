using System.Text.Json.Nodes;
using Cockpit.Core.Backup;

namespace Cockpit.Core.Tests.Backup;

/// <summary>
/// Taking the secrets out of a backup (#70). "Without credentials" is the default, and a default that is only mostly
/// true is worse than no promise at all: the operator drops the archive in a cloud folder precisely because we told
/// them it was safe to.
/// </summary>
public class SecretScrubberTests
{
    [Fact]
    public void AKeyAtTheTop_IsEmptied_AndNamed()
    {
        var settings = JsonNode.Parse("""{"providers":[{"label":"OpenAI","apiKey":"sk-live-abc"}]}""")!;

        var removed = SecretScrubber.Scrub(settings);

        Assert.Empty(settings["providers"]![0]!["apiKey"]!.ToString());
        Assert.Equal(new[] { "providers[0].apiKey" }, removed);
    }

    [Fact]
    public void ATokenInsideAPluginsOwnJson_IsFoundToo()
    {
        // This is where the YouTrack token actually lives: a plugin keeps its settings as a JSON *string* inside the
        // cockpit's JSON. A scrubber that only walked the outer document would report a clean backup and ship the
        // token in it.
        var settings = JsonNode.Parse("""
        {
          "plugins": {
            "youtrack": {
              "Data": {
                "instances": "[{\"Label\":\"Work\",\"InstanceUrl\":\"https://yt.example\",\"Token\":\"perm:secret\"}]"
              }
            }
          }
        }
        """)!;

        var removed = SecretScrubber.Scrub(settings);

        Assert.DoesNotContain("perm:secret", settings.ToJsonString());
        Assert.Single(removed, path => path.Contains("instances"));
    }

    [Fact]
    public void AWebhookAndAPassword_AreSecretsToo()
    {
        var settings = JsonNode.Parse("""{"notifications":{"discordWebhook":"https://discord.com/api/webhooks/1/x"},"smtp":{"password":"hunter2"}}""")!;

        Assert.Equal(2, System.Linq.Enumerable.Count(SecretScrubber.Scrub(settings)));

        Assert.DoesNotContain("hunter2", settings.ToJsonString());
        Assert.DoesNotContain("webhooks/1/x", settings.ToJsonString());
    }

    [Fact]
    public void EverythingThatIsNotASecret_SurvivesUntouched()
    {
        // A scrubber that guessed by value would take the profile's directory for a secret one day, and the restored
        // cockpit would come back subtly wrong instead of visibly incomplete.
        var settings = JsonNode.Parse("""{"profiles":[{"label":"Work","configDir":"/home/raymond/.claude"}],"theme":"dark"}""")!;

        Assert.Empty(SecretScrubber.Scrub(settings));

        Assert.Equal("/home/raymond/.claude", settings["profiles"]![0]!["configDir"]!.ToString());
        Assert.Equal("dark", settings["theme"]!.ToString());
    }

    [Fact]
    public void AnEmptySecret_IsNotReportedAsRemoved()
    {
        // Telling the operator to re-enter a token they never had is how a restore's "what is missing" list becomes
        // noise, and a list nobody reads is a list that hides the one line that mattered.
        var settings = JsonNode.Parse("""{"provider":{"apiKey":""}}""")!;

        Assert.Empty(SecretScrubber.Scrub(settings));
    }

    [Theory]
    [InlineData("token", true)]
    [InlineData("Token", true)]
    [InlineData("apiKey", true)]
    [InlineData("api_key", true)]
    [InlineData("discordWebhook", true)]
    [InlineData("clientSecret", true)]
    [InlineData("label", false)]
    [InlineData("configDir", false)]
    public void WhatCountsAsASecret_IsDecidedByTheFieldsName(string name, bool secret) =>
        Assert.Equal(secret, SecretScrubber.IsSecret(name));

    [Fact]
    public void AWebhookInsideAWorkflowsStep_IsFoundToo()
    {
        // A Slack or Discord step carries its own webhook — in the flow's JSON, inside the workflows plugin's storage,
        // inside cockpit.json. Three layers deep, and still a credential: whoever has it can post as Raymond. A backup
        // that promises "no credentials" and ships one anyway is worth less than no promise at all.
        var flow = """[{"Name":"Release","Nodes":[{"TypeId":"cockpit.slack","Parameters":{"Message":"Deployed","Webhook URL":"https://hooks.example/POSTED-AS-YOU"}}]}]""";

        var settings = new JsonObject
        {
            ["Plugins"] = new JsonObject
            {
                ["workflows"] = new JsonObject
                {
                    ["Data"] = new JsonObject { ["workflows"] = flow },
                },
            },
        };

        var removed = SecretScrubber.Scrub(settings);

        Assert.DoesNotContain("POSTED-AS-YOU", settings.ToJsonString());
        Assert.Single(removed, path => path.Contains("workflows", StringComparison.Ordinal));
    }
}
