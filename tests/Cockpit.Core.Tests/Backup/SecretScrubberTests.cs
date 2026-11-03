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
    public void AKeyAtTheTop_AndATokenInsideAPluginsOwnJson_AreEmptiedAndNamed()
    {
        // The second is where the YouTrack token actually lives: a plugin keeps its settings as a JSON *string* inside
        // the cockpit's JSON. A scrubber that only walked the outer document would report a clean backup and ship the
        // token in it.
        var settings = JsonNode.Parse("""
        {
          "providers": [{ "label": "OpenAI", "apiKey": "sk-live-abc" }],
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

        Assert.Empty(settings["providers"]![0]!["apiKey"]!.ToString());
        Assert.Contains("providers[0].apiKey", removed);
        Assert.DoesNotContain("perm:secret", settings.ToJsonString());
        Assert.Single(removed, path => path.Contains("instances"));
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

    [Theory]
    [InlineData("token", true)]
    [InlineData("Token", true)]
    [InlineData("api_key", true)]
    [InlineData("discordWebhook", true)]
    [InlineData("clientSecret", true)]
    [InlineData("label", false)]
    public void WhatCountsAsASecret_IsDecidedByTheFieldsName(string name, bool secret) =>
        Assert.Equal(secret, SecretScrubber.IsSecret(name));
}
