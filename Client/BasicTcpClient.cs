﻿using System;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using BasicTcp.Events;
using System.Net;
using System.Collections.Generic;

namespace BasicTcp
{
  public class BasicTcpClient : IDisposable
  {
    private TcpClient _Client;
    private NetworkStream _NetworkStream;

    private CancellationTokenSource _TokenSource = new CancellationTokenSource();
    private CancellationToken _Token;

    private readonly SemaphoreSlim _SendLock = new SemaphoreSlim(1, 1);

    private bool _IsConnected = false;
    private ClientEvents _Events = new ClientEvents();

    private bool _IsInitialized = false;

    private readonly uint _AutoReconnectTime = 0;
    private readonly int _Port = 0;

    private readonly IPAddress _IPAddress = null;

    // for data receiving
    private bool _IsHeaderReceived = false;
    private MemoryStream _CurrentReceivingMs = null;
    private long _CurrentReceivingMsSize = 0;
    private Dictionary<string, string> _Header = new Dictionary<string, string>();

    public ClientEvents Events
    {
      get
      {
        return _Events;
      }
      set
      {
        if (value == null) _Events = new ClientEvents();
        else _Events = value;
      }
    }

    public bool IsConnected
    {
      get
      {
        return _IsConnected;
      }
      private set
      {
        if (_IsConnected == value) return;

        _IsConnected = value;

        if (_IsConnected)
        {
          Events.HandleConnected(this);

          if (Timers.IsTimerExist("AutoReconnect")) Timers.Kill("AutoReconnect");
        }
        else
        {
          Events.HandleDisconnected(this);
          if (_AutoReconnectTime != 0) StartAutoReconnect();
        }
      }
    }

    /// <summary>
    /// Initializing tcp client
    /// </summary>
    /// <param name="autoReconnectTime">Time to reconnect to server in MS. Disabled if set to 0.</param>
    public BasicTcpClient(string ip, int port, uint autoReconnectTime = 0)
    {
      if (string.IsNullOrEmpty(ip)) throw new ArgumentNullException(nameof(ip));
      if (port < 0) throw new ArgumentException("Port must be zero or greater.");

      try
      {
        _AutoReconnectTime = autoReconnectTime;

        if (!IPAddress.TryParse(ip, out _IPAddress))
        {
          _IPAddress = Dns.GetHostEntry(ip).AddressList[0];
        }

        _Port = port;

        _Client = new TcpClient
        {
          ReceiveTimeout = 600000,
          SendTimeout = 600000
        };
        _Token = _TokenSource.Token;

        _IsInitialized = true;
      }
      catch (Exception ex)
      {
        if (autoReconnectTime != 0) StartAutoReconnect();
        else throw ex;
      }
    }

    public void Start()
    {
      if (Timers.IsTimerExist("AutoReconnect")) return;
      if (IsConnected) throw new InvalidOperationException("TcpClient already running");
      if (!_IsInitialized) throw new InvalidOperationException("TcpClient not initialized");

      IAsyncResult ar = _Client.BeginConnect(_IPAddress, _Port, null, null);
      WaitHandle wh = ar.AsyncWaitHandle;

      try
      {
        if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5), false))
        {
          _Client.Close();
          _IsInitialized = false;
          Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.ERROR, $"Timeout connecting to {_IPAddress}:{_Port}"));

          throw new TimeoutException("Timeout connecting to " + _IPAddress + ":" + _Port);
        }

        _Client.EndConnect(ar);
        _NetworkStream = _Client.GetStream();

        StartWithoutChecks();
      }
      catch (Exception ex)
      {
        if (_AutoReconnectTime != 0) StartAutoReconnect();
        else throw ex;
      }
      finally
      {
        wh.Close();
      }
    }

    public void Stop()
    {
      if (!IsConnected) throw new InvalidOperationException("TcpClient is not running");

      Dispose();

      if (IsConnected)
      {
        Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.ERROR, "Can't stop client"));
      }
      else
      {
        Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.TCP, "Client successful stopped"));
      }
    }

    public void Dispose()
    {
      try
      {
        if (_TokenSource != null)
        {
          if (!_TokenSource.IsCancellationRequested) _TokenSource.Cancel();
          _TokenSource.Dispose();
          _TokenSource = null;
        }

        if (_NetworkStream != null)
        {
          _NetworkStream.Close();
          _NetworkStream.Dispose();
          _NetworkStream = null;
        }

        if (_Client != null)
        {
          _Client.Close();
          _Client.Dispose();
          _Client = null;
        }
      }
      catch (Exception ex)
      {
        Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.EXCEPTION, "Can't dispose client", ex));
      }

      IsConnected = false;
      _IsInitialized = false;
      GC.SuppressFinalize(this);
    }

    public void Send(string data, Dictionary<string, string> additionalHeaders = null)
    {
      if (string.IsNullOrEmpty(data)) throw new ArgumentNullException(nameof(data));
      if (!_IsConnected) throw new IOException("Client not connected to server.");

      byte[] bytes = Encoding.UTF8.GetBytes(data);
      MemoryStream ms = new MemoryStream();
      ms.Write(bytes, 0, bytes.Length);
      ms.Seek(0, SeekOrigin.Begin);

      _SendLock.Wait();
      SendHeader(bytes.Length, additionalHeaders);
      SendInternal(bytes.Length, ms);
      _SendLock.Release();
    }

    public void Send(byte[] data, Dictionary<string, string> additionalHeaders = null)
    {
      if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
      if (!_IsConnected) throw new IOException("Client not connected to server.");

      MemoryStream ms = new MemoryStream();
      ms.Write(data, 0, data.Length);
      ms.Seek(0, SeekOrigin.Begin);

      _SendLock.Wait();
      SendHeader(data.Length, additionalHeaders);
      SendInternal(data.Length, ms);
      _SendLock.Release();
    }

    public void Send(long contentLength, Stream stream, Dictionary<string, string> additionalHeaders = null)
    {
      if (contentLength < 1) return;
      if (stream == null) throw new ArgumentNullException(nameof(stream));
      if (!stream.CanRead) throw new InvalidOperationException("Cannot read from supplied stream.");
      if (!_IsConnected) throw new IOException("Client not connected to server.");

      _SendLock.Wait();
      SendHeader(contentLength, additionalHeaders);
      SendInternal(contentLength, stream);
      _SendLock.Release();
    }

    private void SendHeader(long contentLength, Dictionary<string, string> additionalHeaders)
    {
      byte[] bytes;

      if (additionalHeaders == null) bytes = Encoding.UTF8.GetBytes($"Content-length:{contentLength}{Environment.NewLine}");
      else
      {
        string headers = "";

        foreach (KeyValuePair<string, string> entry in additionalHeaders)
        {
          headers += $"{entry.Key}:{entry.Value}{Environment.NewLine}";
        }

        bytes = Encoding.UTF8.GetBytes($"Content-length:{contentLength}{Environment.NewLine}{headers}");
      }

      MemoryStream ms = new MemoryStream();
      ms.Write(bytes, 0, bytes.Length);
      ms.Seek(0, SeekOrigin.Begin);

      SendInternal(bytes.Length, ms);

      Task.Delay(30).GetAwaiter().GetResult();
    }

    private void SendInternal(long contentLength, Stream stream)
    {
      long bytesRemaining = contentLength;
      int bytesRead;
      byte[] buffer = new byte[1024];

      try
      {
        while (bytesRemaining > 0)
        {
          bytesRead = stream.Read(buffer, 0, buffer.Length);
          if (bytesRead > 0)
          {
            _NetworkStream.Write(buffer, 0, bytesRead);

            bytesRemaining -= bytesRead;
          }
        }

        _NetworkStream.Flush();
      }
      catch (Exception ex)
      {
        Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.EXCEPTION, "Can't send internal data for server", ex));
      }
    }

    private async Task StartRecieveDataAsync(CancellationToken token)
    {
      try
      {
        while (true)
        {
          if (_Client == null || !_Client.Connected)
          {
            Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.ERROR, "Disconnection detected"));
            _IsConnected = false;
            break;
          }

          byte[] data = await DataReadAsync(token);
          if (data == null)
          {
            await Task.Delay(30);
            continue;
          }

          if (!_IsHeaderReceived)
          {
            _Header = NormalizeRawHeader(Encoding.UTF8.GetString(data));

            if (_Header.ContainsKey("Content-length"))
            {
              _IsHeaderReceived = true;
              _CurrentReceivingMs = new MemoryStream();
              _CurrentReceivingMsSize = Convert.ToInt64(_Header["Content-length"]);
            }
          }
          else
          {
            _CurrentReceivingMs.Write(data);

            if (_CurrentReceivingMs.Length >= _CurrentReceivingMsSize)
            {
              _IsHeaderReceived = false;
              Events.HandleDataReceived(this, new DataReceivedEventArgs(_CurrentReceivingMs.ToArray(), _Header));

              _CurrentReceivingMs = null;
              _CurrentReceivingMsSize = 0;
              _Header.Clear();
            }
          }
        }
      }
      catch (SocketException)
      {
        Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.ERROR, "Data receiver socket exception (disconnection)"));
        IsConnected = false;
      }
      catch (IOException)
      {
        Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.ERROR, "Data receiver io exception (disconnection)"));
        _Client.Close();
        _IsInitialized = false;
        IsConnected = false;
      }
      catch (Exception ex)
      {
        Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.EXCEPTION, "Data receiver exception", ex));
      }
    }

    private async Task<byte[]> DataReadAsync(CancellationToken token)
    {
      if (_Client == null || !_Client.Connected || token.IsCancellationRequested) throw new OperationCanceledException();

      if (!_NetworkStream.CanRead) throw new IOException();

      byte[] buffer = new byte[1024];
      int read;

      using MemoryStream ms = new MemoryStream();
      while (true)
      {
        read = await _NetworkStream.ReadAsync(buffer, 0, buffer.Length);

        if (read > 0)
        {
          ms.Write(buffer, 0, read);
          return ms.ToArray();
        }
        else
        {
          throw new SocketException();
        }
      }
    }

    private void StartAutoReconnect()
    {
      Timers.Create("AutoReconnect", _AutoReconnectTime, false, () =>
      {
        if (_Client != null && _Client.Connected) return;

        if (!_IsInitialized)
        {
          try
          {
            _Client = new TcpClient(_IPAddress.ToString(), _Port)
            {
              SendTimeout = 600000,
              ReceiveTimeout = 600000
            };

            _NetworkStream = _Client.GetStream();
            _Token = _TokenSource.Token;

            _IsInitialized = true;

            StartWithoutChecks();
          }
          catch (Exception ex)
          {
            Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.EXCEPTION, "Can't reconnect to server...", ex));
            StartAutoReconnect();
          }
        }
        else
        {
          try
          {
            _Client.Connect(_IPAddress, _Port);

            if (_Client.Connected) StartWithoutChecks();
          }
          catch (Exception ex)
          {
            Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.EXCEPTION, "Can't reconnect to server...", ex));
            StartAutoReconnect();
          }
        }
      });
    }

    private void StartWithoutChecks()
    {
      _TokenSource = new CancellationTokenSource();
      _Token = _TokenSource.Token;

      Task.Run(() => StartRecieveDataAsync(_Token), _Token);

      IsConnected = true;
      Events.HandleClientLog(this, new ClientLoggerEventArgs(LogType.TCP, "Client successful connected to server"));
    }

    private Dictionary<string, string> NormalizeRawHeader(string rawHeader)
    {
      string[] headerLines = rawHeader.Split(Environment.NewLine);

      Dictionary<string, string> header = new Dictionary<string, string>();

      foreach (string line in headerLines)
      {
        if (string.IsNullOrEmpty(line)) continue;

        string[] lineInfo = line.Split(":");

        if (header.ContainsKey(lineInfo[0])) continue;

        header.Add(lineInfo[0], lineInfo[1]);
      }

      return header;
    }
  }
}
