﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using Macrometa;
using BestHTTP.WebSocket;
using UnityEngine.Networking;
using System.Linq;
using Random = UnityEngine.Random;
using System.Diagnostics;
using UnityEngine.Serialization;

//need to finish Connection distinary at bottom
// need event queue
public class GDNNetworkDriverMulti : MonoBehaviour {

    private const string streamPostFix = "_Multi";
    
    public static bool overrideIsServer = false;
    public static bool overrideIsServerValue = false;
    public static int maxNumberOfClients = 4; // change to 16 for live
    
    public static bool isSocketPingOn= false; // must be set before webSocket is opened
    public static bool isStatsOn= false; //off by default for compatibility 
    public static bool sendDummyTraffic = false; //off by default for compatibility

    public GDNData baseGDNData;
    public string gameName = "FPSGameXX";
    public bool isServer = false;
    public int testId;

    public ListStream listStream;
    public WebSocket consumer1;
    public WebSocket producer1;
    public WebSocket[] serverProducers = new WebSocket[maxNumberOfClients];
    public StreamStats consumer1Stats;
    public StreamStats producer1Stats;// for use by client
    public StreamStats[] producer1StatsArray = new StreamStats[maxNumberOfClients];
    public string consumerName = "Server";
    public string serverName;
    public int ttl = 1;
    public double discardMinutes = -2;// discard messages more than X minutes old should be a negative number 
    public int port;
    
    //Connection handling
    public int increasePauseConnectionError = 10;
    public bool isWaitingDebug = false;
    private bool _isWaiting;
    private const string GDNDriverPing = "GDNDriver";
    
    public bool isWaiting {
        get => _isWaiting;
        set {
            _isWaiting = value;
            if (isWaitingDebug) {
                GameDebug.Log("isWaiting: " + value);
            }
        }
    }

    [SerializeField] 
    private int _currentNetworkErrors;
    
    public int currentNetworkErrors {
        get => _currentNetworkErrors;
        set {
            _currentNetworkErrors = value;
            if (currentNetworkErrors == 0) {
                pauseNetworkError = basePauseNetworkError;
            }
            if (isWaitingDebug) {
                GameDebug.Log("NetworkError: " + value);
                pauseNetworkErrorUntil = Time.time + pauseNetworkError;
            }
        }
    }

    public float basePauseNetworkError = 1f;
    public float pauseNetworkErrorMultiplier = 2f;
    public float pauseNetworkError = 1f;
    public float pauseNetworkErrorUntil = 0f;
    public bool isNetworkErrorPause = false;

    //init progress
    public bool clearAllBacklogsDone = false;
    public bool setTTLDone = false;
    public bool streamListDone = false;
    public bool serverInStreamExists = false;
    public bool serverOutStreamExists = false;
    public bool producerExists = false;
    public bool[] serverProducerExists = new bool[maxNumberOfClients];
    public bool consumerExists = false;
    public bool sendConnect = true;
    public bool setupComplete = false;
    public bool pingStarted = false;

    protected string serverInStreamName;
    protected string serverOutStreamName;
    protected string consumerStreamName;
    protected string producerStreamName;

    // for latency & bandwidth testing
    public float pingFrequency = 1; // should not be less than 1 since it
                                    // only handles a sing ping at a time
                                    // need more stop watches for more pings
                                    
    public float dummyFrequency = 0.05f; // FPSSample standard is 20 messages per second
    public int dummySize = 50; // FPSSample standard is under 2000 bytes per second
    public string nodeId = "";

    public PingStatsGroup[] pingStatsGroups = new PingStatsGroup[maxNumberOfClients];
    public int statsGroupSize;
    public int dummyTrafficQuantity;
    
    public void Awake() {
        
    BestHTTP.HTTPManager.Setup();
    BestHTTP.HTTPManager.MaxConnectionPerServer = 64;
        //var configGDNjson = Resources.Load<TextAsset>("configGDN");
        var defaultConfig = RwConfig.ReadConfig();
        RwConfig.Flush();
        baseGDNData = defaultConfig.gdnData;
        gameName = defaultConfig.gameName; 
        statsGroupSize = defaultConfig.statsGroupSize;
        testId = defaultConfig.testId;
        if (statsGroupSize < 1) {
            statsGroupSize = 900; //15 minutes
        }
        dummyTrafficQuantity = defaultConfig.dummyTrafficQuantity;
        if (dummyTrafficQuantity < 0) {
            dummyTrafficQuantity = 0; 
        }
        //increased traffic should be simulated using multiple streams not increased traffic quantity
        if (dummyTrafficQuantity > 1) {
            dummyTrafficQuantity = 1; 
        }
        GameDebug.Log("multi dummyTrafficQuantity: " + dummyTrafficQuantity);
        nodeId = PingStatsGroup.NodeFromGDNData(baseGDNData);
        GameDebug.Log("multi Setup: " + nodeId);
        if (overrideIsServer) {
            isServer = overrideIsServerValue;
        }
        else {
            isServer = defaultConfig.isServer;
        }

        serverInStreamName = gameName + streamPostFix + "_InStream";
        serverOutStreamName = gameName + streamPostFix + "_OutStream_" ;
        serverName = consumerName;
       
        if (isServer) {
            consumerStreamName = serverInStreamName;
            // will have an array out outStreams & names
            producerStreamName = serverOutStreamName; 
        }
        else {
            consumerStreamName = serverOutStreamName +testId ;
            producerStreamName = serverInStreamName;
            setRandomClientName();
        }
        
        LogFrequency.AddLogFreq("consumer1.OnMessage",1," multi consumer1.OnMessage data: ",3);
        LogFrequency.AddLogFreq("ProducerSend Data",1,"multi ProducerSend Data: ",3);

        isStatsOn &= isSocketPingOn && isServer;
    }
    
    
    void Update() {
        SetupLoopBody();
    }
    
    public void setRandomClientName() {
        consumerName = "C" + (10000000 + Random.Range(1, 89999999)).ToString();
    }

    public void SetupLoopBody() {
        if (pauseNetworkErrorUntil > Time.time) return;
        if (currentNetworkErrors >= increasePauseConnectionError) {
            pauseNetworkError *= pauseNetworkErrorMultiplier;
            
            return;
        }
        /*
        if (isNetworkErrorPause) return;
        if (pauseNetworkErrorUntil > Time.time) return;
        if (currentNetworkErrors >= maxConnectionError) {
            isNetworkErrorPause = true;
            //Debug.LogError("Network problem Game Paused");
            //AddText("Sorry Network there are problems try restarting\n");
            return;
        }
*/
        if (isWaiting) return;
/*
        if (!clearAllBacklogsDone) {
            ClearAllBacklogs();
            return;
        }
*/
        if (!setTTLDone) {
            SetTTL(ttl);
            return;
        }
        
        if (!streamListDone) {
            GetListStream();
            return;
        }

        if (!serverInStreamExists) {
            CreatServerInStream();
            return;
        }

        if (!serverOutStreamExists) {
            CreatServerOutStream();
            return;
        }

        if (isServer) {
            for (int i = 0; i < maxNumberOfClients; i++) {
                if (!serverProducerExists[i]) {
                    producerExists = false;
                   
                    CreateProducerServer(producerStreamName,i);
                    return;
                }
            }
            
        }
        else {
            if (!producerExists) {
                CreateProducer(producerStreamName);
                return;
            }
        }
        
        if (!consumerExists) {
            CreateConsumer(consumerStreamName, consumerName);
            return;
        }

            

        if (!setupComplete) {
            GameDebug.Log("multi Set up Complete as " + gameName + " : " + consumerName);
            setupComplete = true;
            GDNTransport.setupComplete = true;
        }
        if (!sendConnect && !isServer) {
            GameDebug.Log("multi Connect after complete " + gameName + " : " + consumerName);
            Connect();
            sendConnect = true;
            
        }
        if (isSocketPingOn && !pingStarted ) {
            pingStarted = true;
            if (isStatsOn) {
                PingStatsGroup.Init(Application.dataPath, "LatencyStats",statsGroupSize);
                InitPingStatsGroup();
                GameDebug.Log("Multi isSocketPingOn: " + PingStatsGroup.latencyGroupSize);
                StartCoroutine(RepeatTransportPing());
                
            }
            
        }
        
    }

    public void SetTTL(int ttl = 3) {
        isWaiting = true;
        StartCoroutine(MacrometaAPI.SetTTL(baseGDNData, ttl, SetTTLCallBack));
    }
    
    
    public void SetTTLCallBack(UnityWebRequest www) {
        isWaiting = false;
        if (www.isHttpError || www.isNetworkError) {
            GameDebug.Log("SetTTL : " + www.error);
            currentNetworkErrors++;
        }
        else {

            var baseHtttpReply = JsonUtility.FromJson<BaseHtttpReply>(www.downloadHandler.text);
            if (baseHtttpReply.error == true) {
                currentNetworkErrors++;
            }
            else {
                setTTLDone = true;
                currentNetworkErrors = 0;
            }
        }
    }

    public void ClearAllBacklogs() {
        isWaiting = true;
        StartCoroutine(MacrometaAPI.ClearAllBacklogs(baseGDNData, ClearAllBacklogsCallBack));
    }
    
    
    public void ClearAllBacklogsCallBack(UnityWebRequest www) {
        isWaiting = false;
        if (www.isHttpError || www.isNetworkError) {
            GameDebug.Log("ClearAllBacklogs : " + www.error);
            currentNetworkErrors++;
            GameDebug.Log("ClearAllBacklogs URL: " + baseGDNData.ClearAllBacklogs());
        }
        else {

            var baseHtttpReply = JsonUtility.FromJson<BaseHtttpReply>(www.downloadHandler.text);
            if (baseHtttpReply.error == true) {
                GameDebug.Log("ClearAllBacklogs failed:" + baseHtttpReply.code);
                currentNetworkErrors++;
            }
            else {
                clearAllBacklogsDone = true;
                currentNetworkErrors = 0;
            }
        }
    }
    
    public void GetListStream() {
        //Debug.Log(baseGDNData.ListStreamsURL());
        isWaiting = true;
        StartCoroutine(MacrometaAPI.ListStreams(baseGDNData, ListStreamCallback));
    }

    public void ListStreamCallback(UnityWebRequest www) {
        isWaiting = false;
        if (www.isHttpError || www.isNetworkError) {
            currentNetworkErrors++;
            GameDebug.Log("ListStream : " + www.error);
        }
        else {

            //overwrite does not assign toplevel fields
            //JsonUtility.FromJsonOverwrite(www.downloadHandler.text, listStream);
            listStream = JsonUtility.FromJson<ListStream>(www.downloadHandler.text);
            if (listStream.error == true) {
                GameDebug.Log("ListStream failed:" + listStream.code);
                //Debug.LogWarning("ListStream failed reply:" + www.downloadHandler.text);
                currentNetworkErrors++;
            }
            else {
                GameDebug.Log("ListStream succeed " );
                streamListDone = true;
                currentNetworkErrors = 0;
            }
        }
    }
    
    
    public void CreatServerInStream() {
        var gsListName = baseGDNData.StreamListName(serverInStreamName);
        serverInStreamExists = listStream.result.Any(item => item.topic == gsListName);
        if (!serverInStreamExists) {
            isWaiting = true; ;
            GameDebug.Log("Try create: " + serverInStreamName);
            //Debug.Log("creating server in stream: " + baseGDNData.CreateStreamURL(serverInStreamName));
            StartCoroutine(MacrometaAPI.CreateStream(baseGDNData, serverInStreamName, CreateServerInStreamCallback));
        }
    }

    public void CreateServerInStreamCallback(UnityWebRequest www) {
        isWaiting = false;
        if (www.isHttpError || www.isNetworkError) {
            GameDebug.Log("CreateServerInStream : " + www.error);
            currentNetworkErrors++;
            streamListDone= false;
        }
        else {

            var baseHtttpReply = JsonUtility.FromJson<BaseHtttpReply>(www.downloadHandler.text);
            if (baseHtttpReply.error == true) {
                GameDebug.Log("create ServerIn stream failed:" + baseHtttpReply.code);
                currentNetworkErrors++;
                streamListDone= false;
            }
            else {
                GameDebug.Log("create ServerIn stream ");
                currentNetworkErrors = 0;
            }
        }
    }

    
    public void CreatServerOutStream() {
        for (int i = 0; i < maxNumberOfClients; i++) {
            var gsListName = baseGDNData.StreamListName(serverOutStreamName+i);
            serverOutStreamExists = listStream.result.Any(item => item.topic == gsListName);
            if (!serverOutStreamExists) {
                isWaiting = true;
                GameDebug.Log("Try create: " + serverOutStreamName + i);
                StartCoroutine(MacrometaAPI.CreateStream(baseGDNData, serverOutStreamName+i,
                    CreateServerOutStreamCallback));
            }
        }
    }

    public void CreateServerOutStreamCallback(UnityWebRequest www) {
        isWaiting = false;
        if (www.isHttpError || www.isNetworkError) {
            GameDebug.Log("CreateServerOutStream error: " + www.error);
            currentNetworkErrors++;
            streamListDone= false;
        }
        else {

            var baseHtttpReply = JsonUtility.FromJson<BaseHtttpReply>(www.downloadHandler.text);
            if (baseHtttpReply.error == true) {
                GameDebug.Log("create Server Out stream failed:" + baseHtttpReply.code);
                currentNetworkErrors++;
                streamListDone= false;
            }
            else {
                GameDebug.Log("CreateServerOutStream ");
                currentNetworkErrors = 0;
            }
        }
    }

    
    public void CreateProducer(string streamName) {
        isWaiting = true;
        producer1Stats = new StreamStats() {
            dataType = DataType.JSON,
            streamName = streamName,
            streamType = StreamType.Exclusive
        } ;
        StartCoroutine(MacrometaAPI.Producer(baseGDNData, streamName, SetProducer));
    }

    public void CreateProducerServer(string streamName, int id) {
        isWaiting = true;
        producer1StatsArray[id] = new StreamStats() {
            dataType = DataType.JSON,
            streamName = streamName + id,
            streamType = StreamType.Exclusive
        };
        StartCoroutine(MacrometaAPI.ProducerMulti(baseGDNData,
            streamName + id, id, SetProducerMulti));
        
    }
    
    public void SetProducer(WebSocket ws, string debug = "") {
        producer1 = ws;
        if (isSocketPingOn) {
            ws.StartPingThread = true;
            ws.CloseAfterNoMessage = TimeSpan.FromSeconds(10);
        }
        producer1.OnOpen += (o) => {
            isWaiting = false;
            producerExists = true;
            GameDebug.Log("Open " + debug);
        };

        producer1.OnError += (sender, e) => {
            GameDebug.Log("WebSocket Error" + debug + " : " + e);
             if (producer1 != null && producer1.IsOpen) {
                producer1.Close();
            }
            else {
                 GameDebug.Log("WebSocket " + debug);
                producerExists = false;
                isWaiting = false;
            }
        };

        producer1.OnClosed += (socket, code, message) => {
            producerExists = false;
            isWaiting = false;
            GameDebug.Log("Produce closed: " + code + " : " + message);
        };
        producer1.Open();
    }
    
    public void SetProducerMulti(WebSocket ws, string debug = "",int id= 0) {
        serverProducers[id] = ws;
        if (isSocketPingOn) {
            ws.StartPingThread = true;
        }
        serverProducers[id].OnOpen += (o) => {
            isWaiting = false;
            serverProducerExists[id] = true;
            GameDebug.Log("Open " + debug);
        };

        serverProducers[id].OnError += (sender, e) => {
            GameDebug.Log("WebSocket Error" + debug + " : " + e);
            if (serverProducers[id] != null && serverProducers[id].IsOpen) {
                serverProducers[id].Close();
            }
            else {
                GameDebug.Log("WebSocket " + debug);
                serverProducerExists[id] = false;
                isWaiting = false;
            }
        };

        serverProducers[id].OnClosed += (socket, code, message) => {
            serverProducerExists[id] = false;
            isWaiting = false;
            GameDebug.Log("Produce closed: " + code + " : " + message);
        };
        serverProducers[id].Open();
    }
    public void ProducerSend(int id, VirtualMsgType msgType, byte[] payload,
        int pingId = 0, int pingTimeR = 0, int pingTimeO = 0, string aNodeId = "") {
        if (!gdnConnections.ContainsKey(id)) {
            GameDebug.Log("ProducerSend bad id:" + id);
        }
        var properties = new MessageProperties() {
            t = msgType,
            p = gdnConnections[id].port,
            d = gdnConnections[id].destination,
            s = gdnConnections[id].source,
            z = payload.Length
            
        };
        if (msgType == VirtualMsgType.Ping || msgType == VirtualMsgType.Pong) {
            properties.i = pingId;
            properties.r = pingTimeR;
            properties.o = pingTimeO;
            properties.n = aNodeId;
        }
        var message = new SendMessage() {
            properties = properties,
            payload = Convert.ToBase64String(payload)
        };
        string msgJSON = JsonUtility.ToJson(message);
        //GameDebug.Log("Send msg: " + message.properties.msgType);
        if(msgType == VirtualMsgType.Data){
            LogFrequency.Incr("ProducerSend Data", 
                message.payload.Length,
            payload.Length,0
                );}

        if (isServer) {
            serverProducers[gdnConnections[id].port].Send(msgJSON);
        }
        else {
            producer1.Send(msgJSON);
        }

        if (isStatsOn) {
            if (isServer) {
                producer1StatsArray[id].IncrementCounts(msgJSON.Length);
            }
            else {
                producer1Stats.IncrementCounts(msgJSON.Length);
            }
        }
    }
    
    public void CreateConsumer(string streamName, string consumerName) {
        isWaiting = true;
        consumer1Stats = new StreamStats() {
            dataType = DataType.JSON,
            streamName = streamName,
            streamType = StreamType.Shared
        } ;
        StartCoroutine(MacrometaAPI.Consumer(baseGDNData, streamName, consumerName, SetConsumer));
    }

     public void SetConsumer(WebSocket ws, string debug = "") {

        consumer1 = ws;
        consumer1.StartPingThread = isSocketPingOn;
        consumer1.OnOpen += (o) => {
            GameDebug.Log("Open " + debug + " isPingOn: "+ isSocketPingOn);
            consumerExists = true;
            isWaiting = false;
        };
        
        consumer1.OnMessage += (sender, e) => {
            var receivedMessage = JsonUtility.FromJson<ReceivedMessage>(e);
            //Debug.Log("low time: " + DateTime.Now.AddMinutes(discardMinutes));
            //Debug.Log("published: " + DateTime.Parse(receivedMessage.publishTime));
            //GameDebug.Log("recieved msg: " + e);
            if (isStatsOn) {
                consumer1Stats.IncrementCounts(e.Length);
                //GameDebug.Log("Consumer1.OnMessage");
            }

            if (receivedMessage.properties != null && 
                receivedMessage.properties.d == consumerName 
                && DateTime.Now.AddMinutes(discardMinutes) < DateTime.Parse(receivedMessage.publishTime)  
                ) {
                //GameDebug.Log("Consumer1.OnMessage 2");
                switch (receivedMessage.properties.t) {
                    case VirtualMsgType.Data:
                   
                        //GameDebug.Log("Consumer1.OnMessage Data");
                        var connection = new GDNConnection() {
                            source = receivedMessage.properties.d,
                            destination = receivedMessage.properties.s,
                            port = receivedMessage.properties.p
                        };

                        var id = GetConnectionId(connection);
                        if (id == -1) {
                            //GameDebug.Log("discarded message unknown source " 
                            //              + receivedMessage.properties.source);
                            break;
                        }

                        var driverTransportEvent = new DriverTransportEvent() {
                            connectionId = id,
                            data = Convert.FromBase64String(receivedMessage.payload),
                            dataSize = Convert.FromBase64String(receivedMessage.payload).Length,
                            type = DriverTransportEvent.Type.Data
                        };
                        PushEventQueue(driverTransportEvent);
                        LogFrequency.Incr("consumer1.OnMessage", receivedMessage.payload.Length,
                            receivedMessage.properties.z, driverTransportEvent.data.Length);


                        break;
                    case VirtualMsgType.Connect:
                        //GameDebug.Log("Consumer1.OnMessage Connect: " + receivedMessage.properties.s);
                        if (isServer) {
                            ConnectClient(receivedMessage);
                        }

                        break;
                    case VirtualMsgType.Disconnect:
                        //GameDebug.Log("Consumer1.OnMessage Disonnect: " + receivedMessage.properties.s);
                        if (isServer) {
                           //need disconnect 
                        }

                        break;
                    case VirtualMsgType.Ping :
                        //GameDebug.Log("Consumer1.OnMessage Ping ");
                        SendTransportPong(receivedMessage);
                        break;
                    
                    case VirtualMsgType.Pong :
                        //GameDebug.Log("Consumer1.OnMessage Pong ");
                        ReceiveTransportPong(receivedMessage);
                        break;
                    
                    case VirtualMsgType.Internal:
                        //GameDebug.Log("Consumer1.OnMessage internal ");
                        ReceiveInternal(receivedMessage);
                        break;
                    
                    case VirtualMsgType.Dummy:
                        //GameDebug.Log("Consumer1.OnMessage dummy ");
                        ReceiveDummy(receivedMessage);
                        break;
                }
            }
        
            //ttl is set low but not working and So FPSSample does need this 

            var ackMessage = new AckMessage() {
                messageId = receivedMessage.messageId
            };
            var msgString = JsonUtility.ToJson(ackMessage);
            consumer1.Send(msgString);
            //Debug.Log("ack: " + debug + " : " + msgString);


        };
        consumer1.OnError += (sender, e) => {
            GameDebug.Log("WebSocket Error" + debug + " : " + e);
            
            //Debug.Log("producer1: " + producer1);
            //Debug.Log("IsOpen: " + producer1?.IsOpen.ToString());
            if (producer1 != null && producer1.IsOpen) {
                producer1.Close();
            }
            else {
                consumerExists = false;
                isWaiting = false;
            }
        };
        
        consumer1.OnClosed += (socket, code, message) => {
            consumerExists = false;
            isWaiting = false;
        };
        
        consumer1.Open();

    }
     
     public void ConnectClient(ReceivedMessage receivedMessage) {
         var connection = new GDNConnection() {
             source = receivedMessage.properties.d,
             destination = receivedMessage.properties.s,
             port = receivedMessage.properties.p 
         };

         var id = AddOrGetConnectionId(connection);
         var driverTransportEvent = new DriverTransportEvent() {
             connectionId = id,
             data = new byte[0],
             dataSize = 0,
             type = DriverTransportEvent.Type.Connect
         };
         PushEventQueue(driverTransportEvent);
         if (sendDummyTraffic) {
             StartCoroutine(RepeatDummyMsg()); 
             GameDebug.Log("dummy size:"+ dummySize + " freq: " + dummyFrequency);
         }
         GameDebug.Log("ConnectClient id: " + id + " Q: " + driverTransportEvents.Count);
         
     }
     
     public void DisconnectClient(ReceivedMessage receivedMessage) {
         var connection = new GDNConnection() {
             source = receivedMessage.properties.d,
             destination = receivedMessage.properties.s,
             port = receivedMessage.properties.p 
         };

         var id = AddOrGetConnectionId(connection);
         RemoveConnectionId(id);
         var driverTransportEvent = new DriverTransportEvent() {
             connectionId = id,
             data = new byte[0],
             dataSize = 0,
             type = DriverTransportEvent.Type.Disconnect
         };
         
         PushEventQueue(driverTransportEvent);
         GameDebug.Log("DisconnectClient id: " + id + " Q: " + driverTransportEvents.Count);
     }

     public void Connect() {
         if (isServer) return; 
         var connection = new GDNConnection() {
             source = consumerName,
             destination = serverName,
             port = testId 
         };

         var id = AddOrGetConnectionId(connection);
         GameDebug.Log(" Client send Connect");
         ProducerSend(id, VirtualMsgType.Connect, new byte[0]);
         if (sendDummyTraffic) {
             StartCoroutine(RepeatDummyMsg()); 
             GameDebug.Log("dummy size:"+ dummySize + " freq: " + dummyFrequency);
         }
     }

     public Dictionary<int,GDNConnection> gdnConnections = new Dictionary<int,GDNConnection>();

     public struct GDNConnection {
         public string source;
         public string destination;
         public int port;
         public int id;
     }

     public int AddOrGetConnectionId(GDNConnection gdnConnection) {
        foreach(var kvp in gdnConnections) {
            if (kvp.Value.destination == gdnConnection.destination &&
                kvp.Value.port == gdnConnection.port) {
                return kvp.Value.id;
            }
        }
        IEnumerable<int> query = from id in gdnConnections.Keys  
            orderby id  
            select id;

        int min = 0;
        foreach (var id in query) {
            if (id != min) {
                break;
            }
            else {
                min++;
            }
        }
        
        gdnConnection.id = min;
        gdnConnections[min] = gdnConnection;
        return min;
     }
     
     public int GetConnectionId(GDNConnection gdnConnection) {
         foreach(var kvp in gdnConnections) {
             if (kvp.Value.destination == gdnConnection.destination &&
                 kvp.Value.port == gdnConnection.port) {
                 return kvp.Value.id;
             }
         }
         IEnumerable<int> query = from id in gdnConnections.Keys  
             orderby id  
             select id;

         int min = 0;
         foreach (var id in query) {
             if (id != min) {
                 break;
             }
             else {
                 min++;
             }
         }
         if (!gdnConnections.ContainsKey(min)) {
             return -1;
         }
         gdnConnection.id = min;
         return min;
     }
    
     public void RemoveConnectionId(int connectionID) {
         if (gdnConnections.ContainsKey(connectionID)) {
             gdnConnections.Remove(connectionID);
         }
     }
     
     public struct DriverTransportEvent {
         public enum Type {
             Data,
             Connect,
             Disconnect,
             Empty,
         }
         public Type type;
         public int connectionId;
         public byte[] data;
         public int dataSize;
     }
     
     public void PushEventQueue(DriverTransportEvent driverTransportEvent) {
         driverTransportEvents.Enqueue(driverTransportEvent);
     }
     public DriverTransportEvent PopEventQueue() {
         if (driverTransportEvents.Count == 0) {
             return new DriverTransportEvent() {
                 type = DriverTransportEvent.Type.Empty,
                 connectionId = -1,
                 data = new byte[0],
                 dataSize = 0
             };
         }
         return driverTransportEvents.Dequeue();
     }
     
     public Queue<DriverTransportEvent> driverTransportEvents = new Queue<DriverTransportEvent>();

     #region Latency & bandwidth testing

     public void InitPingStatsGroup() {
         for (int i = 0; i < maxNumberOfClients; i++) {
             pingStatsGroups[i] = new PingStatsGroup();
             pingStatsGroups[i].InitStatsFromGDNDate(baseGDNData);
             pingStatsGroups[i].SetStreamStats(consumer1Stats, true);
             pingStatsGroups[i].SetStreamStats(producer1StatsArray[i], false);
             pingStatsGroups[i].connectionID = i;
         }
     }
     
     
     private IEnumerator RepeatTransportPing() {
         for(;;){
             SendTransportPing();
             yield return new WaitForSeconds(pingFrequency);
             
         }
     }
     
     public void ReceiveTransportPong(ReceivedMessage receivedMessage) {
         var transportPing = TransportPings.Remove(receivedMessage.properties.i);
         if (isStatsOn) {
             pingStatsGroups[transportPing.destinationId].AddRtt( transportPing.elapsedTime,
                 serverProducers[transportPing.destinationId].Latency, consumer1.Latency,
                 receivedMessage.properties.o, receivedMessage.properties.r,
                 receivedMessage.properties.n);
         }
     }
     
     public void SendTransportPing() {
         if(gdnConnections.Count==0){
             return;
         }

         foreach (var desitnationId in gdnConnections.Keys) {
             var pingId = TransportPings.Add(desitnationId, Time.realtimeSinceStartup, 0);
             ProducerSend(desitnationId, VirtualMsgType.Ping, new byte[0], pingId);
             //GameDebug.Log("SendTransportPing: " + desitnationId + " : " + pingId);
         }
     }
     public void SendTransportPong(ReceivedMessage receivedMessage) {
         var connection = new GDNConnection() {
             source = receivedMessage.properties.d,
             destination = receivedMessage.properties.s,
             port = receivedMessage.properties.p
         };

         var id = GetConnectionId(connection);
         if (id == -1) {
             //GameDebug.Log("discarded message unknown source " 
             //              + receivedMessage.properties.source);
             return;
         }
         //GameDebug.Log("SendTransportPong: " +id + " : "+ receivedMessage.properties.i);
         
         ProducerSend(id, VirtualMsgType.Pong, new byte[0], receivedMessage.properties.i,
             producer1.Latency,consumer1.Latency,nodeId);
     }
     
     private IEnumerator RepeatDummyMsg() {
         for(;;){
             for (int i = 0; i < dummyTrafficQuantity; i++) {
                 SendDummy();
             }
             yield return new WaitForSeconds(dummyFrequency);
             
         }
     }
     
     public void SendDummy() {
         if(gdnConnections.Count==0){
             return;
         }
         int desitnationId = gdnConnections.Keys.First();
         System.Random rnd = new System.Random();
         byte[] data = new byte[dummySize]; // convert kb to byte
         rnd.NextBytes(data);
         ProducerSend(desitnationId, VirtualMsgType.Dummy, data);
         
     }
     
     
     
     public void ReceiveInternal(ReceivedMessage receivedMessage) {
         //process internal message
         //for update ping stats for client stream
     }
     
     public void ReceiveDummy(ReceivedMessage receivedMessage) {
         //store dummy stats somewhere
     }
     #endregion
     
}
