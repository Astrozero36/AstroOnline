using System.Collections.Generic;
using UnityEngine;

public sealed class UdpClientConnect : MonoBehaviour
{
    // v0.043: allow UI to read the active client without reflection
    public NetClient Client => _client;

    // Step 45.1b: last computed interpolation error (avg, units) for non-local entities
    public float LastInterpAvgError { get; private set; }

    private NetClient _client;

    // Others: interpolation
    private const ulong InterpDelayTicks = 3;

    // Must match server movement (mirrors server-side ProtocolConstants)
    private static readonly float MoveSpeed = ProtocolConstants.MoveSpeed;
    private static readonly float TickDt = ProtocolConstants.TickDt;

    private readonly Dictionary<ulong, List<NetSnapshot>> _historyByClient = new();
    private readonly Dictionary<ulong, GameObject> _entityByClient = new();

    // Prediction
    private uint _localSeq;
    private uint _lastAckSeq;
    private bool _hasAckBase;

    private Vector3 _authoritativeBasePos;
    private Vector3 _predictedPos;

    private readonly Dictionary<uint, InputSample> _inputBySeq = new();

    private float _accum;

    // Step 41: terminal stop for non-recoverable rejects (protocol mismatch)
    private bool _terminalStop;
    private string _terminalStopMessage;

    // Step 45.1b: interpolation error instrumentation (no logging)
    private float _interpErrorAccum;
    private int _interpErrorSamples;
    private float _nextInterpSampleTime;

    private void Start()
    {
        var config = new NetClientConfig
        {
            ServerIp = "127.0.0.1",
            ServerPort = 7777,
            PingIntervalMs = 1000
        };

        _client = new NetClient(config);

        // Subscribe so rejects/state are visible in Unity console.
        _client.OnStateChanged += OnNetStateChanged;
        _client.OnRejected += OnNetRejected;

        _client.Start();

        _nextInterpSampleTime = Time.time + 1f;
        LastInterpAvgError = 0f;
    }

    private void OnNetStateChanged(ConnectionState state)
    {
        Debug.Log($"NET STATE: {state}");

        // Step 41: if NetClient entered terminal state, stop status spam and surface a clear message once
        if (state == ConnectionState.UpdateRequired)
        {
            if (!_terminalStop)
            {
                _terminalStop = true;

                if (string.IsNullOrEmpty(_terminalStopMessage))
                    _terminalStopMessage = "Client out of date. Update required.";

                Debug.LogError(_terminalStopMessage);
            }
        }
    }

    private void OnNetRejected(RejectReason reason, byte expected, byte got)
    {
        Debug.LogWarning($"NET REJECT: reason={reason} expectedVersion={expected} gotVersion={got}");

        // Step 41: Protocol mismatch is terminal (must not reconnect)
        if (reason == RejectReason.ProtocolVersionMismatch)
        {
            if (!_terminalStop)
            {
                _terminalStop = true;
                _terminalStopMessage = $"Client out of date (expectedVersion={expected}, gotVersion={got}). Update required.";
                Debug.LogError(_terminalStopMessage);
            }
        }
    }

    private void Update()
    {
        if (_client == null)
            return;

        // Step 41: stop gameplay updates once terminal
        if (_terminalStop)
            return;

        // Drain snapshots
        while (_client.TryDequeueSnapshot(out var s))
        {
            // history for non-local interpolation
            if (!_historyByClient.TryGetValue(s.ClientId, out var list))
            {
                list = new List<NetSnapshot>(32);
                _historyByClient.Add(s.ClientId, list);
            }

            InsertSortedByTick(list, s);
            if (list.Count > 64)
                list.RemoveRange(0, list.Count - 64);

            // local reconciliation base from acked seq
            if (_client.IsConnected && _client.ClientId != 0 && s.ClientId == _client.ClientId)
            {
                _authoritativeBasePos = new Vector3(s.X, s.Y, s.Z);

                uint newAck = s.LastInputSeqAck;
                if (!_hasAckBase)
                {
                    _hasAckBase = true;
                    _lastAckSeq = newAck;
                    _predictedPos = _authoritativeBasePos;
                    _inputBySeq.Clear();
                    _localSeq = newAck; // start seq from server-acked baseline
                    _accum = 0f;
                }
                else if (newAck > _lastAckSeq)
                {
                    _lastAckSeq = newAck;

                    // Rebuild predicted from authoritative + replay inputs AFTER ack
                    ReplayFromAck();
                }
            }
        }

        if (!_client.IsConnected || !_hasAckBase || _client.ClientId == 0)
        {
            RenderFromInterpolationOnly();
            SampleInterpMetricOncePerSecond();
            return;
        }

        // Sample input on main thread
        float inputX = 0f;
        float inputZ = 0f;
        if (Input.GetKey(KeyCode.A)) inputX -= 1f;
        if (Input.GetKey(KeyCode.D)) inputX += 1f;
        if (Input.GetKey(KeyCode.W)) inputZ += 1f;
        if (Input.GetKey(KeyCode.S)) inputZ -= 1f;

        // Step local prediction at 20 Hz and send matching seq
        _accum += Time.unscaledDeltaTime;

        while (_accum >= TickDt)
        {
            _accum -= TickDt;

            _localSeq++;

            _inputBySeq[_localSeq] = new InputSample(inputX, inputZ);

            // Predict locally
            Integrate(ref _predictedPos, inputX, inputZ);

            // Send the EXACT SAME seq/input to server
            _client.SendInput(_localSeq, inputX, inputZ);
        }

        // Render: local predicted, others interpolated
        RenderAll();

        // Step 45.1b: compute metric once per second
        SampleInterpMetricOncePerSecond();
    }

    private void SampleInterpMetricOncePerSecond()
    {
        if (Time.time < _nextInterpSampleTime)
            return;

        _nextInterpSampleTime = Time.time + 1f;

        if (_interpErrorSamples > 0)
            LastInterpAvgError = _interpErrorAccum / _interpErrorSamples;
        else
            LastInterpAvgError = 0f;

        _interpErrorAccum = 0f;
        _interpErrorSamples = 0;
    }

    private void ReplayFromAck()
    {
        _predictedPos = _authoritativeBasePos;

        // Replay inputs from (ack+1 .. localSeq)
        for (uint seq = _lastAckSeq + 1; seq <= _localSeq; seq++)
        {
            if (_inputBySeq.TryGetValue(seq, out var inp))
                Integrate(ref _predictedPos, inp.X, inp.Z);
            else
                Integrate(ref _predictedPos, 0f, 0f);
        }

        // Drop old inputs <= ack
        var toRemove = new List<uint>();
        foreach (var k in _inputBySeq.Keys)
        {
            if (k <= _lastAckSeq)
                toRemove.Add(k);
        }
        for (int i = 0; i < toRemove.Count; i++)
            _inputBySeq.Remove(toRemove[i]);
    }

    private void RenderAll()
    {
        foreach (var kv in _historyByClient)
        {
            ulong cid = kv.Key;
            var hist = kv.Value;
            if (hist.Count == 0)
                continue;

            Vector3 pos;

            if (cid == _client.ClientId)
            {
                pos = _predictedPos;
            }
            else
            {
                if (!TryGetInterpolated(hist, out _, out var x, out var y, out var z))
                    continue;

                pos = new Vector3(x, y, z);

                // Step 45.1b: measure interpolation drift vs latest snapshot (metric only)
                var last = hist[hist.Count - 1];
                var snapPos = new Vector3(last.X, last.Y, last.Z);
                float err = Vector3.Distance(snapPos, pos);
                _interpErrorAccum += err;
                _interpErrorSamples++;
            }

            var go = GetOrCreateEntity(cid);
            go.transform.position = pos;
        }
    }

    private void RenderFromInterpolationOnly()
    {
        if (_historyByClient.Count == 0)
            return;

        foreach (var kv in _historyByClient)
        {
            var hist = kv.Value;
            if (hist.Count == 0)
                continue;

            if (!TryGetInterpolated(hist, out _, out var x, out var y, out var z))
                continue;

            var go = GetOrCreateEntity(kv.Key);
            go.transform.position = new Vector3(x, y, z);
        }
    }

    private static void Integrate(ref Vector3 pos, float inputX, float inputZ)
    {
        float magSq = (inputX * inputX) + (inputZ * inputZ);
        if (magSq > 1f)
        {
            float invMag = 1f / Mathf.Sqrt(magSq);
            inputX *= invMag;
            inputZ *= invMag;
        }

        pos.x += inputX * MoveSpeed * TickDt;
        pos.z += inputZ * MoveSpeed * TickDt;
    }

    private GameObject GetOrCreateEntity(ulong clientId)
    {
        if (_entityByClient.TryGetValue(clientId, out var go) && go != null)
            return go;

        go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = $"NetEntity_{clientId}";
        go.transform.position = Vector3.zero;

        if (_client != null && _client.IsConnected && clientId == _client.ClientId)
            go.transform.localScale = new Vector3(1.1f, 1.1f, 1.1f);

        _entityByClient[clientId] = go;
        return go;
    }

    private static void InsertSortedByTick(List<NetSnapshot> list, NetSnapshot s)
    {
        if (list.Count == 0 || s.ServerTick >= list[list.Count - 1].ServerTick)
        {
            list.Add(s);
            return;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (s.ServerTick >= list[i].ServerTick)
            {
                list.Insert(i + 1, s);
                return;
            }
        }

        list.Insert(0, s);
    }

    private static bool TryGetInterpolated(List<NetSnapshot> hist, out ulong renderTick, out float x, out float y, out float z)
    {
        var latest = hist[hist.Count - 1];
        renderTick = latest.ServerTick > InterpDelayTicks ? (latest.ServerTick - InterpDelayTicks) : 0;

        if (renderTick <= hist[0].ServerTick)
        {
            x = hist[0].X; y = hist[0].Y; z = hist[0].Z;
            return true;
        }

        if (renderTick >= latest.ServerTick)
        {
            x = latest.X; y = latest.Y; z = latest.Z;
            return true;
        }

        NetSnapshot a = hist[0];
        NetSnapshot b = latest;

        for (int i = 0; i < hist.Count - 1; i++)
        {
            var s0 = hist[i];
            var s1 = hist[i + 1];
            if (s0.ServerTick <= renderTick && renderTick <= s1.ServerTick)
            {
                a = s0;
                b = s1;
                break;
            }
        }

        ulong dt = b.ServerTick - a.ServerTick;
        if (dt == 0)
        {
            x = a.X; y = a.Y; z = a.Z;
            return true;
        }

        float alpha = (float)(renderTick - a.ServerTick) / (float)dt;

        x = Mathf.LerpUnclamped(a.X, b.X, alpha);
        y = Mathf.LerpUnclamped(a.Y, b.Y, alpha);
        z = Mathf.LerpUnclamped(a.Z, b.Z, alpha);
        return true;
    }

    private void OnDestroy()
    {
        if (_client != null)
        {
            _client.OnStateChanged -= OnNetStateChanged;
            _client.OnRejected -= OnNetRejected;
            _client.Dispose();
        }

        foreach (var kv in _entityByClient)
        {
            if (kv.Value != null)
                Destroy(kv.Value);
        }

        _entityByClient.Clear();
        _historyByClient.Clear();
        _inputBySeq.Clear();
    }

    private readonly struct InputSample
    {
        public readonly float X;
        public readonly float Z;

        public InputSample(float x, float z)
        {
            X = x;
            Z = z;
        }
    }
}
