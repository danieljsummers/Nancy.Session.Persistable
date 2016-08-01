# Nancy.Session.Persistable

This is a community project that provides the majority of a session store implementation for Nancy, the lean-and-mean
web framework that runs under the .NET framework.  It also provides extensions to the Nancy ```Request``` object that
allow strongly-typed retrieval of objects from the session.  It uses cookies to associate the session Id with a
request.

## Get It ![NuGet Version](https://img.shields.io/nuget/v/Nancy.Session.Persistable.svg)
This package will be installed when you install any of the available back-end storage packages. Currently available:
* RethinkDB | [project](https://github.com/danieljsummers/Nancy.Session.RethinkDB) | 
  [package](https://nuget.org/packages/Nancy.Session.RethinkDB) ![NuGet Version](https://img.shields.io/nuget/v/Nancy.Session.RethinkDB.svg)
* MongoDB | [project](https://github.com/danieljsummers/Nancy.Session.MongoDB) | 
  [package](https://nuget.org/packages/Nancy.Session.MongoDB) ![NuGet Version](https://img.shields.io/nuget/v/Nancy.Session.MongoDB.svg)
* InMemory | [project](https://github.com/danieljsummers/Nancy.Session.InMemory) |
  [package](https://nuget.org/packages/Nancy.Session.InMemory) ![NuGet Version](https://img.shields.io/nuget/v/Nancy.Session.InMemory.svg)
* Relational / EF | [project](https://github.com/danieljsummers/Nancy.Session.Relational) |
  package ![NuGet Version](https://img.shields.io/nuget/v/Nancy.Session.Relational.svg)

_NOTE: v0.8.x builds are done in debug mode, and may have some console logging during use. For v0.9.x, we will switch
to release mode, and these logs will be gone.  Also, while the API is currently thought stable, it may change up until
a 1.0 release._

_NOTE 2: Possible future implementations include Redis and RavenDB._

## Enable It

To enable sessions, you have to
[override the default Nancy bootstrapper](https://github.com/NancyFx/Nancy/wiki/Bootstrapper).  This sounds way
scarier than it actually is; you can do it in just a few lines of code.  Following their lead, persistable sessions are
fully [SDHP](https://github.com/NancyFx/Nancy#the-super-duper-happy-path)-compliant.

You can do it in C#...
```csharp
namespace ExampleNancyApp
{
    using Nancy;
    using Nancy.Bootstrapper;
    using Nancy.Session.Persistable;
    using Nancy.TinyIoc;

    public class ApplicationBootstrapper : DefaultNancyBootstrapper
    {
        protected override void ApplicationStartup(TinyIoCContainer container, IPipelines pipelines)
        {
            base.ApplicationStartup(container, pipelines);

            // Enable sessions
            PersistableSessions.Enable(pipelines, [config]);
        }
    }
}
```

... or F# ...

```fsharp
module ExampleNancyApp

open Nancy
open Nancy.Bootstrapper
open Nancy.Session.Persistable

type ApplicationBootstrapper() =
  inherit DefaultNancyBootstrapper()
  override this.ApplicationStartup (container, pipelines) =
    base.ApplicationStartup (container, pipelines)
    // Enable sessions
    PersistableSessions.Enable (pipelines, [config])
```

... or even Visual Basic.NET!

```vb.net
Imports Nancy
Imports Nancy.Bootstrapper
Imports Nancy.Session.Persistable
Imports Nancy.TinyIoc

Namespace ExampleNancyApp

    Public Class ApplicationBootstrapper
        Inherits DefaultNancyBootstrapper

        Protected Overrides Sub ApplicationStartup(container As TinyIoCContainer, pipelines As IPipelines)
            MyBase.ApplicationStartup(container, pipelines)
            ' Enable sessions
            PersistableSessions.Enable(pipelines, [config])
        End Sub

    End Class

End Namespace
```

The store-specific ```[config]``` object is detailed in each implementation project.  Each of these objects takes an
optional ```Nancy.Cryptography.CryptographyConfiguration``` parameter that is used to control the cryptography used for
the session Id cookie.  If it is not specified, it uses a default configuration.

_Retrieving the current session needs to happen early in the request cycle, and persisting it needs to happen as late as
possible.  So, in your bootstrapper, put the ```PersistableSessions.Enable``` call as late as possible._


## Use It

The project adds a ```PersistableSession``` property (F#) or method (C# / VB.NET) on the Nancy ```Request``` object.
This returns an ```IPersistableSession```, which extends Nancy's ```ISession``` interface with a few additional methods.

* **Get&lt;T&gt;()** can be used to retrieve a strongly-typed item from the session.  If the item does not exist, this
method will return ```null``` (or a default value for value types).

* **GetOrDefault&lt;T&gt;()** works the same way as ```Get<T>()```, but you specify the value you want back if there's
  not a value in the session.

## Configure It

Each implementation comes with its own configuration object, but they all share some common options.

**UseRollingSessions** (bool - default: true)

A rolling session is one where the expiration is reset every time the session is accessed, making the expiration into a
rolling window. A non-rolling session is only good from the time of its creation, and goes away at the end of that
period, no matter how active the session is.

**Expiry** (TimeSpan - default: 2 hours)

This is how long (based on either creation or access time) the session will last.

**ExpiryCheckFrequency** (TimeSpan - default: 1 minute)

This is how frequently expired sessions are removed from the store.  Each attempt to load or save a session can trigger
this, but this throttle keeps it from running on every request.

---

