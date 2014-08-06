﻿#region File Information
/********************************************************************
  Project: ServiceBusMQManager
  File:    NServiceBusMSMQManager.cs
  Created: 2012-09-23

  Author(s):
    Daniel Halan

 (C) Copyright 2012 Ingenious Technology with Quality Sweden AB
     all rights reserved

********************************************************************/
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Messaging;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using NLog;
using NServiceBus;
using NServiceBus.Persistence.Raven.SubscriptionStorage;
using NServiceBus.Tools.Management.Errors.ReturnToSourceQueue;
using NServiceBus.Transports.Msmq;
using Raven.Client.Document;
//using NServiceBus;
//using NServiceBus.Utils;
using ServiceBusMQ.Manager;
using ServiceBusMQ.Model;
using ServiceBusMQ.NServiceBus;
using ServiceBusMQ.NServiceBus.MSMQ;


namespace ServiceBusMQ.NServiceBus4 {

  //[PermissionSetAttribute(SecurityAction.LinkDemand, Name = "FullTrust")]
  public class NServiceBus_MSMQ_Manager : NServiceBusManagerBase<MsmqMessageQueue4>, ISendCommand, IViewSubscriptions {

    protected Logger _log = LogManager.GetCurrentClassLogger();

    protected List<QueueItem> EMPTY_LIST = new List<QueueItem>();


    public override string ServiceBusName { get { return "NServiceBus"; } }
    public override string ServiceBusVersion { get { return "4"; } }
    public override string MessageQueueType { get { return "MSMQ"; } }

    public static readonly string CS_SERVER = "server";
    public static readonly string CS_RAVEN_PERSISTANCE = "ravenPersistence";
    public static readonly string CS_PEEK_THREADS = "peakThreads";


    private bool IsPeekThreadsEnabled {
      get {
        return (bool)_connectionSettings.GetValue(NServiceBus_MSMQ_Manager.CS_PEEK_THREADS, false);
      }
    }

    class PeekThreadParam {
      public Queue Queue { get; set; }
      public MessageQueue MsmqQueue { get; set; }
    }

    bool _terminated = false;
    
    public NServiceBus_MSMQ_Manager() {
    }

    public override void Initialize(Dictionary<string, object> connectionSettings, Queue[] monitorQueues, SbmqmMonitorState monitorState) {
      base.Initialize(connectionSettings, monitorQueues, monitorState);

      LoadQueues();

      if( IsPeekThreadsEnabled )
        StartPeekThreads();
    }
    public override void Terminate() {
      _terminated = true;
    }


    void StartPeekThreads() {
      foreach( QueueType qt in Enum.GetValues(typeof(QueueType)) ) {

        if( qt != QueueType.Error ) {
          foreach( var q in GetQueueListByType(qt) ) {
            var t = new Thread(new ParameterizedThreadStart(PeekMessages));
            if( q.Main.CanRead ) {
              t.Name = "peek-msmq-" + q.GetDisplayName();
              t.Start(new PeekThreadParam() { MsmqQueue = q.Main, Queue = q.Queue });
            }
          }


        }
      }
    }

    object _peekItemsLock = new object();
    List<QueueItem> _peekedItems = new List<QueueItem>();


    public void PeekMessages(object prm) {
      PeekThreadParam p = prm as PeekThreadParam;
      string qName = p.MsmqQueue.GetDisplayName();
      uint sameCount = 0;
      string lastId = string.Empty;

      bool _isPeeking = false;

      SetupMessageReadPropertyFilters(p.MsmqQueue, p.Queue.Type);

      p.MsmqQueue.PeekCompleted += (source, asyncResult) => {
        if( _monitorState.IsMonitoring(p.Queue.Type) ) {
          Message msg = p.MsmqQueue.EndPeek(asyncResult.AsyncResult);

          if( msg.Id == lastId )
            sameCount++;

          else {
            sameCount = 0;
            TryAddItem(msg, p.Queue);
          }

          if( lastId != msg.Id )
            lastId = msg.Id;

        }
        _isPeeking = false;
      };

      while( !_terminated ) {

        while( !_monitorState.IsMonitoring(p.Queue.Type) ) {
          Thread.Sleep(1000);

          if( _terminated )
            return;
        }


        if( !_isPeeking ) {

          if( sameCount > 0 ) {
            if( sameCount / 10.0F == 1.0F )
              Thread.Sleep(100);

            else if( sameCount / 100.0F == 1.0F )
              Thread.Sleep(200);

            else if( sameCount % 300 == 0 )
              Thread.Sleep(500);
          }
          p.MsmqQueue.BeginPeek();
          _isPeeking = true;
        }

        Thread.Sleep(100);
      }


    }

    private bool TryAddItem(Message msg, Queue q) {

      lock( _peekItemsLock ) {

        if( !_peekedItems.Any(i => i.Id == msg.Id) ) {

          var itm = CreateQueueItem(q, msg);

          if( PrepareQueueItemForAdd(itm) )
            _peekedItems.Add(itm);


          return true;

        } else return false;
      }

    }



    private void LoadQueues() {
      _monitorQueues.Clear();

      foreach( var queue in MonitorQueues )
        AddMsmqQueue(_connectionSettings["server"] as string, queue);

    }
    private void AddMsmqQueue(string serverName, Queue queue) {
      try {
        _monitorQueues.Add(new MsmqMessageQueue4(serverName, queue));

      } catch( Exception e ) {
        OnError("Error occured when loading queue: '{0}\\{1}'\n\r".With(serverName, queue.Name), e, false);
      }
    }


    private void SetupMessageReadPropertyFilters(MessageQueue q, QueueType type) {

      q.MessageReadPropertyFilter.Id = true;
      q.MessageReadPropertyFilter.ArrivedTime = true;
      q.MessageReadPropertyFilter.Label = true;
      q.MessageReadPropertyFilter.Body = false;

      //if( type == QueueType.Error )
      q.MessageReadPropertyFilter.Extension = true;
    }


    public override QueueFetchResult GetUnprocessedMessages(QueueFetchUnprocessedMessagesRequest req) {
      var result = new QueueFetchResult();
      var queues = _monitorQueues.Where(q => q.Queue.Type == req.Type);

      if( queues.Count() == 0 ) {
        result.Items = EMPTY_LIST;
        return result;
      }

      List<QueueItem> r = new List<QueueItem>();
      result.Items = r;
      uint totalCount = 0;
      foreach( var q in queues ) {
        var msmqQueue = q.Main;

        if( IsIgnoredQueue(q.Queue.Name) || !q.Main.CanRead )
          continue;


        // Add peaked items
        if( IsPeekThreadsEnabled ) {
          lock( _peekItemsLock ) {
            if( _peekedItems.Count > 0 ) {

              r.AddRange(_peekedItems);
              _peekedItems.Clear();
            }
          }
        }


        r.AddRange(GetMessagesFromQueue(q, q.Main, req.CurrentItems, ref totalCount));

        //var list = GetMessagesFromQueue(q, q.Retries, req.CurrentItems, ref totalCount);
        //_log.Debug("{0} Retries: {1}".With(q.GetDisplayName(), list.Count()));
        //foreach( var itm in list ) 
        //  _log.Debug("Retry msg: " + itm.DisplayName);
        
        //r.AddRange(list);

        //list = GetMessagesFromQueue(q, q.Timeouts, req.CurrentItems, ref totalCount);
        //_log.Debug(q.GetDisplayName() + " Timeouts: " + list.Count());
        //r.AddRange(list);
        //foreach( var itm in list )
        //  _log.Debug("Timeouts msg: " + itm.DisplayName);

        //list = GetMessagesFromQueue(q, q.TimeoutsDispatcher, req.CurrentItems, ref totalCount);
        //_log.Debug(q.GetDisplayName() + " TimeoutsDispatcher: " + list.Count());
        //r.AddRange(list);
        //foreach( var itm in list )
        //  _log.Debug("TimeoutsDispatcher msg: " + itm.DisplayName);

      }
      result.Count = totalCount;

      return result;
    }

    private IEnumerable<QueueItem> GetMessagesFromQueue(MsmqMessageQueue4 q, MessageQueue mq, IEnumerable<QueueItem> currentItems, ref uint totalCount) {
      List<QueueItem> r = new List<QueueItem>();

      SetupMessageReadPropertyFilters(mq, q.Queue.Type);

      try {
        var msgs = q.GetAllMessages();
        totalCount += (uint)msgs.Length;
        //result.Count += (uint)msgs.Length;

        foreach( var msg in msgs ) {

          QueueItem itm = currentItems.FirstOrDefault(i => i.Id == msg.Id);

          if( itm == null && !r.Any(i => i.Id == msg.Id) ) {
            itm = CreateQueueItem(q.Queue, msg);

            // Load Message names and check if its not an infra-message
            if( !PrepareQueueItemForAdd(itm) )
              itm = null;
          }

          if( itm != null )
            r.Insert(0, itm);

          // Just fetch first 500
          if( r.Count > SbmqSystem.MAX_ITEMS_PER_QUEUE )
            break;
        }

      } catch( Exception e ) {
        OnError("Error occured when processing queue " + q.Queue.Name + ", " + e.Message, e, false);
      }

      return r;
    }

    public override QueueFetchResult GetProcessedMessages(QueueType type, DateTime since, IEnumerable<QueueItem> currentItems) {
      var result = new QueueFetchResult();

      var queues = GetQueueListByType(type);
      if( queues.Count() == 0 ) {
        result.Items = EMPTY_LIST;
        return result;
      }
      List<QueueItem> r = new List<QueueItem>();
      result.Items = r;

      foreach( var q in queues ) {
        string qName = q.GetDisplayName();

        if( IsIgnoredQueue(qName) || !q.CanReadJournalQueue )
          continue;

        SetupMessageReadPropertyFilters(q.Journal, type);

        try {
          List<Message> messages = new List<Message>();

          // Enumete from the earliest item
          MessageEnumerator msgs = q.Journal.GetMessageEnumerator2();
          try {
            while( msgs.MoveNext() ) {
              Message msg = msgs.Current;

              if( msg.ArrivedTime >= since )
                messages.Add(msg);
            }
          } finally {
            msgs.Close();
          }

          foreach( var msg in messages ) {
            QueueItem itm = currentItems.FirstOrDefault(i => i.Id == msg.Id);

            if( itm == null ) {
              itm = CreateQueueItem(q.Queue, msg);
              itm.Processed = true;

              if( !PrepareQueueItemForAdd(itm) )
                itm = null;
            }

            if( itm != null )
              r.Insert(0, itm);
          }



        } catch( Exception e ) {
          OnError("Error occured when getting processed messages from queue \"" + qName + "\", " + e.Message, e, false);
        }

      }

      result.Count = (uint)r.Count;

      return result;
    }


    /// <summary>
    /// Called when we know that we actually shall add the item, and here we can execute processes that takes extra time
    /// </summary>
    /// <param name="itm"></param>
    /// <returns></returns>
    private bool PrepareQueueItemForAdd(QueueItem itm) {

      // Ignore control messages
      if( itm.Headers.ContainsKey(Headers.ControlMessageHeader) && Convert.ToBoolean(itm.Headers[Headers.ControlMessageHeader]) )
        return false;

      // Get Messages names
      if( itm.Headers.ContainsKey("NServiceBus.EnclosedMessageTypes") ) {
        itm.Messages = ExtractEnclosedMessageTypeNames(itm.Headers["NServiceBus.EnclosedMessageTypes"]);

      } else { // Get from Message body
        if( itm.Content == null )
          LoadMessageContent(itm);

        itm.Messages = GetMessageNames(itm.Content, false);
      }
      itm.DisplayName = MergeStringArray(itm.Messages).Default(itm.DisplayName).CutEnd(55);

      if( itm.Queue.Type == QueueType.Error && itm.Headers.ContainsKey( global::NServiceBus.Faults.FaultsHeaderKeys.FailedQ )) {
        itm.OriginQueueName = itm.Headers[global::NServiceBus.Faults.FaultsHeaderKeys.FailedQ];
      }

      // Get process started time
      if( itm.Headers.ContainsKey("NServiceBus.ProcessingStarted") && itm.Headers.ContainsKey("NServiceBus.ProcessingEnded") ) {

        try {
          itm.ProcessTime = Convert.ToInt32(( Convert.ToDateTime(itm.Headers["NServiceBus.ProcessingEnded"]) -
                            Convert.ToDateTime(itm.Headers["NServiceBus.ProcessingStarted"]) ).TotalSeconds);

        } catch {
#if DEBUG
          Console.WriteLine("Failed to parse NServiceBus.ProcessingStarted");
#endif
        }

      }

      // Get Error message info
      if( itm.Headers.ContainsKey("NServiceBus.ExceptionInfo.Message") ) {

        itm.Error = new QueueItemError();
        try {
          itm.Error.State = itm.Queue.Type == QueueType.Error ? QueueItemErrorState.ErrorQueue : QueueItemErrorState.Retry;
          itm.Error.Message = itm.Headers["NServiceBus.ExceptionInfo.Message"];

          if( itm.Headers.ContainsKey("NServiceBus.ExceptionInfo.StackTrace") )
            itm.Error.StackTrace = itm.Headers["NServiceBus.ExceptionInfo.StackTrace"];

          if( itm.Headers.ContainsKey(Headers.Retries) )
            itm.Error.Retries = Convert.ToInt32(itm.Headers[Headers.Retries]);

          //itm.Error.TimeOfFailure = Convert.ToDateTime(itm.Headers.SingleOrDefault(k => k.Key == "NServiceBus.TimeOfFailure").Value);
        } catch {
          itm.Error = null;
        }
      }



      return true;
    }

    private static readonly XmlSerializer headerSerializer = new XmlSerializer(typeof(List<HeaderInfo>));

    private QueueItem CreateQueueItem(Queue queue, Message msg) {
      var itm = new QueueItem(queue);
      itm.DisplayName = msg.Label;
      itm.Id = msg.Id;
      itm.ArrivedTime = msg.ArrivedTime;
      //itm.Content = ReadMessageStream(msg.BodyStream);

      itm.Headers = new Dictionary<string, string>();
      if( msg.Extension.Length > 0 ) {
        var stream = new MemoryStream(msg.Extension);
        var o = headerSerializer.Deserialize(stream);

        foreach( var pair in o as List<HeaderInfo> )
          if( pair.Key != null )
            itm.Headers.Add(pair.Key, pair.Value);
      }


      return itm;
    }


    private MsmqMessageQueue4 GetMessageQueue(QueueItem itm) {
      return _monitorQueues.Single(i => i.Queue.Type == itm.Queue.Type && i.Queue.Name == itm.Queue.Name);
    }

    public override string LoadMessageContent(QueueItem itm) {
      if( itm.Content == null ) {

        MsmqMessageQueue4 msmq = GetMessageQueue(itm);

        msmq.LoadMessageContent(itm);
      }

      return itm.Content;
    }


    public override MessageSubscription[] GetMessageSubscriptions(Dictionary<string, object> connectionSettings, IEnumerable<string> queues) {
      var server = connectionSettings[CS_SERVER] as string;
      List<MessageSubscription> r = new List<MessageSubscription>();

      // Raven Persistance
      DocumentStore db = null;
      try {
        var ravenUrl = (string)connectionSettings.GetValue(NServiceBus_MSMQ_Manager.CS_RAVEN_PERSISTANCE, null);
        if( !ravenUrl.IsValid() )
          ravenUrl = "http://" + server + ":8080";

        db = new DocumentStore {
          Url = ravenUrl
        };
        db.Initialize();
      } catch {
        return r.ToArray();
      }


      // MSMQ Persistance
      var msmqQ = MessageQueue.GetPrivateQueuesByMachine(server).Where(q => q.QueueName.EndsWith(".subscriptions")).Select(q => q.QueueName);

      foreach( var queueName in queues ) {
        var queueSubscr = queueName + ".subscriptions";

        // First check MSMQ
        if( msmqQ.Any(mq => mq.EndsWith(queueSubscr)) ) {

          MessageQueue q = ServiceBusMQ.NServiceBus.MSMQ.Msmq.Create(server, queueSubscr, QueueAccessMode.ReceiveAndAdmin);

          q.MessageReadPropertyFilter.Label = true;
          q.MessageReadPropertyFilter.Body = true;

          try {
            foreach( var msg in q.GetAllMessages() ) {

              var itm = new MessageSubscription();
              itm.FullName = GetSubscriptionType(ReadMessageStream(msg.BodyStream));
              itm.Name = ParseClassName(itm.FullName);
              itm.Subscriber = msg.Label;
              itm.Publisher = queueName;

              r.Add(itm);
            }
          } catch( Exception e ) {
            OnError("Error occured when getting subcriptions", e, true);
          }


        } else { // RavenDB

          var headers = db.DatabaseCommands.Head("Raven/Databases/" + queueName);
          if( headers != null ) {

            using( var s = db.OpenSession(queueName) ) {

              //s.Advanced.AllowNonAuthoritativeInformation = false;
              //var doc = s.Load<Subscription>("subscriptions/88dc7793-0cbd-ec39-5dd2-6b982e1e3c76");
              //if( doc != null ) 
              //  Console.Write("WOW");

              var list = s.Advanced.LoadStartingWith<Subscription>("subscription", null, 0, 1024).ToArray();

              foreach( var subr in list ) { //.StartsWith("Subscriptions/")) ) {

                foreach( var client in subr.Clients ) {

                  var itm = new MessageSubscription();
                  itm.FullName = subr.MessageType.ToString();
                  itm.Name = ParseClassName(itm.FullName);
                  itm.Subscriber = client.ToString();
                  itm.Publisher = queueName;

                  r.Add(itm);
                }

              }
            }
          }



        }


      }




      //foreach( var queueName in MessageQueue.GetPrivateQueuesByMachine(server).
      //                                      Where(q => q.QueueName.EndsWith(".subscriptions")).Select(q => q.QueueName) ) {

      //}

      return r.ToArray();
    }

    private string ParseClassName(string asmName) {

      if( asmName.IsValid() ) {

        int iEnd = asmName.IndexOf(',');
        int iStart = asmName.LastIndexOf('.', iEnd);

        if( iEnd > -1 && iStart > -1 ) {
          iStart++;
          return asmName.Substring(iStart, iEnd - iStart);
        }

      }

      return asmName;
    }

    public override void PurgeErrorMessages(string queueName) {
      //string name = "private$\\" + queueName;

      _monitorQueues.Where(q => q.Queue.Type == QueueType.Error && q.Queue.Name == queueName).Single().Purge();

      OnItemsChanged();
    }
    public override void PurgeErrorAllMessages() {
      var items = _monitorQueues.Where(q => q.Queue.Type == QueueType.Error);

      if( items.Count() > 0 ) {
        items.ForEach(q => q.Purge());

        OnItemsChanged();
      }

    }

    public override void PurgeMessage(QueueItem itm) {
      MessageQueue q = GetMessageQueue(itm);

      if( q != null ) {
        q.ReceiveById(itm.Id);

        itm.Processed = true;

        OnItemsChanged();
      }
    }
    public override void PurgeAllMessages() {
      _monitorQueues.ForEach(q => q.Purge());

      OnItemsChanged();
    }



    public override void MoveErrorMessageToOriginQueue(QueueItem itm) {
      if( string.IsNullOrEmpty(itm.Id) )
        throw new ArgumentException("MessageId can not be null or empty");

      if( itm.Queue.Type != QueueType.Error )
        throw new ArgumentException("Queue is not of type Error, " + itm.Queue.Type);

      try {
        var mgr = new ErrorManager();

        mgr.InputQueue = Address.Parse(itm.Queue.Name);

        mgr.ReturnMessageToSourceQueue(itm.Id);

      } catch( Exception e ) {
        throw new Exception("Failed to Move Messages from Error Queue '{0}' to Origin".With(itm.Queue.Name), e);
      }
    }
    public override async Task MoveAllErrorMessagesToOriginQueue(string errorQueue) {
      var mgr = new ErrorManager();

      try {

        if( errorQueue != null ) {
          mgr.InputQueue = Address.Parse(errorQueue);

          mgr.ReturnAll();

        } else { // Return All Error Queues

          foreach( var queue in MonitorQueues.Where(q => q.Type == QueueType.Error) ) {
            mgr.InputQueue = Address.Parse(errorQueue = queue.Name);

            mgr.ReturnAll();
          }

        }
      } catch( Exception e ) {
        throw new Exception("Failed to Move Messages from Error Queue '{0}' to Origin".With(errorQueue), e);
      }

    }



    private static readonly string[] IGNORE_DLL = new string[] { "\\Autofac.dll", "\\AutoMapper.dll", "\\log4net.dll", 
                                                                  "\\MongoDB.Driver.dll", "\\MongoDB.Bson.dll", 
                                                                  "\\NServiceBus.dll" };

    #region Send Command


    public Type[] GetAvailableCommands(string[] asmPaths) {
      return GetAvailableCommands(asmPaths, _commandDef, false);
    }
    public Type[] GetAvailableCommands(string[] asmPaths, CommandDefinition commandDef, bool suppressErrors) {
      List<Type> arr = new List<Type>();


      List<string> nonExistingPaths = new List<string>();


      foreach( var path in asmPaths ) {

        if( Directory.Exists(path) ) {

          foreach( var dll in Directory.GetFiles(path, "*.dll") ) {

            if( IGNORE_DLL.Any(a => dll.EndsWith(a)) )
              continue;

            try {
              var asm = Assembly.LoadFrom(dll);
              //var asm = Assembly.ReflectionOnlyLoadFrom(dll);

              foreach( Type t in asm.GetTypes() ) {

                if( commandDef.IsCommand(t) )
                  arr.Add(t);

              }

            } catch( ReflectionTypeLoadException fte ) {

              if( suppressErrors )
                continue;

              StringBuilder sb = new StringBuilder();
              if( fte.LoaderExceptions != null ) {

                if( fte.LoaderExceptions.All(a => a.Message.EndsWith("does not have an implementation.")) )
                  continue;

                string lastMsg = null;
                foreach( var ex in fte.LoaderExceptions ) {
                  if( ex.Message != lastMsg )
                    sb.AppendFormat(" - {0}\n\n", lastMsg = ex.Message);
                }
              }

              OnWarning("Could not search for Commands in Assembly '{0}'".With(Path.GetFileName(dll)), sb.ToString());

            } catch { }

          }
        } else nonExistingPaths.Add(path);
      }

      if( nonExistingPaths.Count > 0 )
        OnError("The paths '{0}' doesn't exist, could not search for commands.".With(nonExistingPaths.Concat()));


      return arr.ToArray();
    }



    protected IBus _bus;


    public void SetupServiceBus(string[] assemblyPaths, CommandDefinition cmdDef, Dictionary<string, object> connectionSettings) {
      _commandDef = cmdDef;

      Console.Write(typeof(global::NServiceBus.Configure).FullName);

      List<Assembly> asms = new List<Assembly>();

      foreach( string path in assemblyPaths ) {

        foreach( string file in Directory.GetFiles(path, "*.dll") ) {
          try {
            asms.Add(Assembly.LoadFrom(file));
          } catch { }
        }

      }

      if( CommandContentFormat == "XML" ) {
        Configure.Serialization.Xml();

      } else if( CommandContentFormat == "JSON" ) {
        Configure.Serialization.Json();

      }

      _bus = Configure.With(asms)
              .DefineEndpointName("SBMQM_NSB")
              .DefaultBuilder()
        .DefiningCommandsAs(t => _commandDef.IsCommand(t))
              .UseTransport<global::NServiceBus.Msmq>()
              .UnicastBus()
          .SendOnly();

    }


    public override string SerializeCommand(object cmd) {
      try {
        return MessageSerializer.SerializeMessage(cmd, CommandContentFormat);

      } catch( Exception e ) {
        OnError("Failed to Serialize Command to " + CommandContentFormat, e);
        return null;
      }
    }
    public override object DeserializeCommand(string cmd, Type cmdType) {
      try {
        return MessageSerializer.DeserializeMessage(cmd, cmdType, CommandContentFormat);

      } catch( Exception e ) {
        OnError("Failed to Parse Command string as " + CommandContentFormat, e);
        return null;
      }
    }


    public void SendCommand(Dictionary<string, object> connectionStrings, string destinationQueue, object message) {
      var srv = connectionStrings[CS_SERVER] as string;

      if( Tools.IsLocalHost(srv) )
        srv = null;

      string dest = !string.IsNullOrEmpty(srv) ? destinationQueue + "@" + srv : destinationQueue;


      //var assemblies = message.GetType().Assembly
      // .GetReferencedAssemblies()
      // .Select(n => Assembly.Load(n))
      // .ToList();
      //assemblies.Add(GetType().Assembly);


      if( message != null )
        _bus.Send(dest, message);
      else OnError("Can not send an incomplete message");

    }

    #endregion

  }
}
