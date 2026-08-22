using System.Reflection;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Closes the AC-1029 class of bug: <c>SessionDriverFactory</c> builds the host-facing adapter from a
/// registration's <see cref="PluginSessionCapabilities"/>, not the driver instance's own property of the same
/// name, so a flag set only on the driver never reaches the app. Reflects over every <see langword="bool"/>
/// property so a new flag is covered without editing this file, unlike AC-739's hand-picked asserts.
/// </summary>
internal static class PluginCapabilityParityAssert
{
    /// <summary>
    /// Fails when a bool capability differs between <paramref name="registration"/> and <paramref name="driver"/>
    /// unless its property name is passed in <paramref name="acknowledgedDivergences"/> — pass one only alongside
    /// a comment at the call site explaining why the two are meant to differ (e.g. Claude's
    /// <c>ConfinesViaPermissionsOnly</c>, a registration-only concept the driver instance never sets).
    /// </summary>
    public static void AssertMatches(PluginSessionCapabilities registration, PluginSessionCapabilities driver, params string[] acknowledgedDivergences)
    {
        foreach (var property in typeof(PluginSessionCapabilities).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType != typeof(bool) || acknowledgedDivergences.Contains(property.Name))
            {
                continue;
            }

            var registrationValue = (bool)property.GetValue(registration)!;
            var driverValue = (bool)property.GetValue(driver)!;
            Assert.True(
                registrationValue == driverValue,
                $"{property.Name}: registration={registrationValue}, driver={driverValue}. "
                    + "If this is deliberate, pass the property name to acknowledgedDivergences with a comment explaining why.");
        }
    }
}
