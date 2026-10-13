using System.Diagnostics.CodeAnalysis;

// Standing views are experimental, under the diagnostic CHALK003: a host acknowledges it to use them,
// in its project with <NoWarn>$(NoWarn);CHALK003</NoWarn>, or where it uses them with
// #pragma warning disable CHALK003. Until views are declared stable, their behaviour and their API may
// change in any release, and a host should check what a view gives against its statement before it
// relies on it.
[assembly: Experimental("CHALK003")]
