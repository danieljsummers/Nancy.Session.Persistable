# Nancy.Session.Persistable

The [Nancy.Session.RethinkDB](https://github.io/danieljsummers/Nancy.Session.RethinkDB) project is currently in
development.  This project is a dependency of that project, and contains interfaces and extension methods that are
not specific to a RethinkDB implementation of a session store.  The hope is that it can be reused to streamline
development of other session stores (MongoDB, Redis, PostgreSQL, and Entity Framework are all on the possibly-to-do
list).

Currently, this provides:
* an ```IPersistableSession``` interface that specifies a strongly-typed ```Get``` method
* a ```BasePersistableSession``` that implements ```IPersistableSession```; the default implementation of the virtual
  ```Get``` method will attempt to deserialize possibly JSON-serialized objects if the type in the session is not the
  type requested.  *(This is how RethinkDB deserializes the session dictionary; the dictionary is an ```IDictionary```,
  but some items may still be ```JArray```s or ```JToken```s.)*
* the ```Request.PersistableSession``` property (F#) / extension method (C# / VB.NET) that returns an
  ```IPersistableSession``` instead of the ```ISession``` returned by the ```Request.Session``` property.  As
  ```IPersistableSession``` inherits from ```ISession```, they may be used interchangeable unless you are trying to
  get a strongly-typed value from the session.

----

More documentation will be forthcoming.