## Introduction

AsyncMeteor is a Meteor/DDP client for .NET that uses the modern `Task`-based asynchronous programming model. This library is under the MIT license.

## Usage

### Connecting

```
var meteor = new Meteor(new Uri("wss://mywebsite.com/websocket"));
await meteor.Connect(); // Or meteor.Connect(cancellationToken)
```

### Calling methods

```
var eor = await meteor.Call("addTask", "bob", "Buy groceries.", new string[] { "Apples", "Bananas" });
if (eor.Error == null)
{
    Console.WriteLine("Success: " + eor.Result.ToString());
}
else
{
    Console.WriteLine("Error: " + eor.Error.ToString());
}
```

### Subscribing and unsubscribing to publications

```
var tasksCollection = meteor.RegisterCollection("tasks");
var eor = await meteor.Subscribe("tasks", "bob");
if (eor.Error != null)
    throw new Exception(eor.Error.ToString());
var subscription = eor.Subscription;
// At this point, the subscription is ready.

foreach (var pair in tasksCollection.Data)
{
    var id = pair.Key;
    var task = pair.Value;
    Console.WriteLine("Task " + id + ": " + task.name);
    foreach (var item in task.items)
        Console.WriteLine("  Item: " + item);
}

subscription.Unsubscribe();
meteor.UnregisterCollection("tasks");
```

### Observing changes in a collection

```
tasksCollection.Added += tasksCollection_Added;
tasksCollection.Changed += tasksCollection_Changed;
tasksCollection.Removed += tasksCollection_Removed;

// ...

private void tasksCollection_Added(string id, IDictionary<string, object> fields)
{
    Console.WriteLine("Task " + id + " has been added:");
    foreach (var field in fields)
        Console.WriteLine(field.Key + " = " + (field.Value == null ? "null" : field.Value.ToString()));
}

private void tasksCollection_Changed(string id, dynamic oldObject, IDictionary<string, object> fields, string[] cleared)
{
    Console.WriteLine("Task " + id + " has been changed:");
    foreach (var field in fields)
        Console.WriteLine(field.Key + " = " + (field.Value == null ? "null" : field.Value.ToString()));
}

private void tasksCollection_Removed(string id, dynamic oldObject)
{
    Console.WriteLine("Task " + id + " has been removed");
}
```

## Known issues

* Web socket reconnection is not handled yet
* Nested objects in fields are not handled properly
