﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Renci.SshNet.Channels;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp.Messages;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;

namespace Renci.SshNet.Sftp
{
    public class SftpSession : IDisposable
    {
        private Session _session;

        private ChannelSession _channel;

        private List<byte> _data = new List<byte>(32 * 1024);

        private Exception _exception;

        private EventWaitHandle _errorOccuredWaitHandle = new AutoResetEvent(false);

        private EventWaitHandle _sftpVersionConfirmed = new AutoResetEvent(false);

        private TimeSpan _operationTimeout;

        public event EventHandler<ErrorEventArgs> ErrorOccured;

        /// <summary>
        /// Gets remote working directory.
        /// </summary>
        public string WorkingDirectory { get; private set; }

        /// <summary>
        /// Gets SFTP protocol version.
        /// </summary>
        public int ProtocolVersion { get; private set; }

        private uint _requestId;
        /// <summary>
        /// Gets the next request id for sftp session.
        /// </summary>
        public uint NextRequestId
        {
            get
            {
                return this._requestId++;
            }
        }

        #region SFTP messages

        internal event EventHandler<MessageEventArgs<StatusMessage>> StatusMessageReceived;

        internal event EventHandler<MessageEventArgs<DataMessage>> DataMessageReceived;

        internal event EventHandler<MessageEventArgs<HandleMessage>> HandleMessageReceived;

        internal event EventHandler<MessageEventArgs<NameMessage>> NameMessageReceived;

        internal event EventHandler<MessageEventArgs<AttributesMessage>> AttributesMessageReceived;

        #endregion

        public SftpSession(Session session, TimeSpan operationTimeout)
        {
            this._session = session;
            this._operationTimeout = operationTimeout;
        }

        public void Connect()
        {
            this._channel = this._session.CreateChannel<ChannelSession>();

            this._session.ErrorOccured += Session_ErrorOccured;
            this._session.Disconnected += Session_Disconnected;
            this._channel.DataReceived += Channel_DataReceived;

            this._channel.Open();

            this._channel.SendSubsystemRequest("sftp");

            this.SendMessage(new InitMessage(3));

            this.WaitHandle(this._sftpVersionConfirmed, this._operationTimeout);

            this.ProtocolVersion = 3;

            //  Resolve current directory
            this.WorkingDirectory = this.GetRealPath(".");
        }

        public void Disconnect()
        {
            this.Dispose();
        }

        public void ChangeDirectory(string path)
        {
            var fullPath = this.GetCanonicalPath(path);

            //  Open directory
            using (var openCmd = new OpenDirectoryCommand(this, fullPath))
            {
                openCmd.CommandTimeout = this._operationTimeout;

                //  Try to open directory and throw an exception if can't
                openCmd.Execute();

                using (SftpCommand closeCmd = new CloseCommand(this, openCmd.Handle))
                {
                    closeCmd.CommandTimeout = this._operationTimeout;

                    closeCmd.Execute();
                }
            }

            this.WorkingDirectory = fullPath;
        }

        public SftpFileAttributes GetFileAttributes(byte[] handle)
        {
            using (var cmd = new StatusCommand(this, handle))
            {
                cmd.CommandTimeout = this._operationTimeout;

                cmd.Execute();

                return cmd.Attributes;
            }
        }

        public SftpFileAttributes GetFileAttributes(string path)
        {
            using (var cmd = new StatusCommand(this, path))
            {
                cmd.CommandTimeout = this._operationTimeout;

                cmd.Execute();

                return cmd.Attributes;
            }
        }

        public void SetFileAttributes(byte[] handle, SftpFileAttributes attributes)
        {
            using (var cmd = new SetStatusCommand(this, handle, attributes))
            {
                cmd.CommandTimeout = this._operationTimeout;

                cmd.Execute();
            }
        }

        public void SetFileAttributes(string path, SftpFileAttributes attributes)
        {
            using (var cmd = new SetStatusCommand(this, path, attributes))
            {
                cmd.CommandTimeout = this._operationTimeout;

                cmd.Execute();
            }
        }

        internal void SendMessage(SftpMessage sftpMessage)
        {
            this._session.SendMessage(new SftpDataMessage(this._channel.RemoteChannelNumber, sftpMessage));
        }

        private void Channel_DataReceived(object sender, Common.ChannelDataEventArgs e)
        {
            //  Add channel data to internal data holder
            this._data.AddRange(e.Data);

            while (this._data.Count > 4 + 1)
            {
                //  Extract packet length
                var packetLength = (this._data[0] << 24 | this._data[1] << 16 | this._data[2] << 8 | this._data[3]);

                //  Check if complete packet data is available
                if (this._data.Count < packetLength + 4)
                {
                    //  Wait for complete message to arrive first
                    break;
                }
                this._data.RemoveRange(0, 4);

                //  Create buffer to hold packet data
                var packetData = new byte[packetLength];

                //  Cope packet data to array
                this._data.CopyTo(0, packetData, 0, packetLength);

                //  Remove loaded data from _data holder
                this._data.RemoveRange(0, packetLength);

                //  Load SFTP Message and handle it
                dynamic sftpMessage = SftpMessage.Load(packetData);

                try
                {
                    this.HandleMessage(sftpMessage);
                }
                catch (Exception exp)
                {
                    this.RaiseError(exp);
                    break;
                }
            }
        }

        #region Handle SFTP incoming messages and raise appropriate events

        private void HandleMessage(VersionMessage message)
        {
            if (message.Version == 3)
            {
                this._sftpVersionConfirmed.Set();
            }
            else
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "Server SFTP version {0} is not supported.", message.Version));
            }
        }

        private void HandleMessage(StatusMessage message)
        {
            if (this.StatusMessageReceived != null)
            {
                this.StatusMessageReceived(this, new MessageEventArgs<StatusMessage>(message));
            }
        }

        private void HandleMessage(DataMessage message)
        {
            if (this.DataMessageReceived != null)
            {
                this.DataMessageReceived(this, new MessageEventArgs<DataMessage>(message));
            }
        }

        private void HandleMessage(HandleMessage message)
        {
            if (this.HandleMessageReceived != null)
            {
                this.HandleMessageReceived(this, new MessageEventArgs<HandleMessage>(message));
            }
        }

        private void HandleMessage(NameMessage message)
        {
            if (this.NameMessageReceived != null)
            {
                this.NameMessageReceived(this, new MessageEventArgs<NameMessage>(message));
            }
        }

        private void HandleMessage(AttributesMessage message)
        {
            if (this.AttributesMessageReceived != null)
            {
                this.AttributesMessageReceived(this, new MessageEventArgs<AttributesMessage>(message));
            }
        }

        private void HandleMessage(ExtendedMessage message)
        {
            //  Extended messages currently not supported, send appropriate status message
            this.SendMessage(new StatusMessage(message.RequestId, StatusCodes.OperationUnsupported, "Extended messages are not supported", string.Empty));
        }

        #endregion

        public string GetRealPath(string path)
        {
            using (var cmd = new RealPathCommand(this, path))
            {
                cmd.CommandTimeout = this._operationTimeout;

                cmd.Execute();

                if (cmd.Files == null)
                    return null;
                else
                    return cmd.Files.First().FullName;
            }
        }

        private void Session_Disconnected(object sender, EventArgs e)
        {
            this.RaiseError(new SshException("Connection was lost"));
        }

        private void Session_ErrorOccured(object sender, ErrorEventArgs e)
        {
            this.RaiseError(e.GetException());
        }

        internal void WaitHandle(WaitHandle waitHandle, TimeSpan operationTimeout)
        {
            var waitHandles = new WaitHandle[]
                {
                    this._errorOccuredWaitHandle,
                    waitHandle,
                };

            var index = EventWaitHandle.WaitAny(waitHandles, operationTimeout);

            if (index < 1)
            {
                throw this._exception;
            }
            else if (index > 1)
            {
                //  throw time out error
                throw new SshOperationTimeoutException(string.Format(CultureInfo.CurrentCulture, "Sftp operation has timed out."));
            }
        }

        /// <summary>
        /// Resolves path into absolute path on the server.
        /// </summary>
        /// <param name="path">Path to resolve.</param>
        /// <returns>Absolute path</returns>
        internal string GetCanonicalPath(string path)
        {
            var fullPath = path;

            if (!string.IsNullOrEmpty(path) && path[0] != '/' && this.WorkingDirectory != null)
            {
                if (this.WorkingDirectory[this.WorkingDirectory.Length - 1] == '/')
                {
                    fullPath = string.Format(CultureInfo.InvariantCulture, "{0}{1}", this.WorkingDirectory, path);
                }
                else
                {
                    fullPath = string.Format(CultureInfo.InvariantCulture, "{0}/{1}", this.WorkingDirectory, path);
                }
            }

            var canonizedPath = this.GetRealPath(fullPath);

            if (!string.IsNullOrEmpty(canonizedPath))
                return canonizedPath;

            //  Check for special cases
            if (fullPath.EndsWith("/.", StringComparison.InvariantCultureIgnoreCase) ||
                fullPath.EndsWith("/..", StringComparison.InvariantCultureIgnoreCase) ||
                fullPath.Equals("/", StringComparison.InvariantCultureIgnoreCase) ||
                fullPath.IndexOf('/') < 0)
                return fullPath;

            var pathParts = fullPath.Split(new char[] { '/' });

            var partialFullPath = string.Join("/", pathParts, 0, pathParts.Length - 1);

            if (string.IsNullOrEmpty(partialFullPath))
                partialFullPath = "/";

            canonizedPath = this.GetRealPath(partialFullPath);

            if (string.IsNullOrEmpty(canonizedPath))
            {
                return fullPath;
            }
            else
            {
                var slash = string.Empty;
                if (canonizedPath[canonizedPath.Length - 1] != '/')
                    slash = "/";
                return string.Format(CultureInfo.InvariantCulture, "{0}{1}{2}", canonizedPath, slash, pathParts[pathParts.Length - 1]);
            }
        }

        public byte[] OpenFile(string path, Flags flags)
        {
            using (var cmd = new OpenCommand(this, path, flags))
            {
                cmd.CommandTimeout = this._operationTimeout;

                cmd.Execute();

                return cmd.Handle;
            }
        }

        public void CloseHandle(byte[] handle)
        {
            using (var cmd = new CloseCommand(this, handle))
            {
                cmd.CommandTimeout = this._operationTimeout;

                cmd.Execute();
            }
        }

        public void Write(byte[] handle, ulong offset, byte[] data)
        {
            using (var cmd = new WriteCommand(this, handle, offset, data))
            {
                cmd.CommandTimeout = this._operationTimeout;

                cmd.Execute();
            }
        }

        public byte[] Read(byte[] handle, ulong offset, uint length)
        {
            using (var cmd = new ReadCommand(this, handle, offset, length))
            {
                cmd.CommandTimeout = this._operationTimeout;

                cmd.Execute();

                return cmd.Data;
            }
        }

        internal bool FileExistsCommand(string path, Flags flags)
        {
            using (var cmd = new FileExistsCommand(this, path, flags))
            {
                cmd.CommandTimeout = this._operationTimeout;

                cmd.Execute();

                return cmd.Exists;
            }
        }

        private void RaiseError(Exception error)
        {
            this._exception = error;

            this._errorOccuredWaitHandle.Set();

            if (this.ErrorOccured != null)
            {
                this.ErrorOccured(this, new ErrorEventArgs(error));
            }
        }

        #region IDisposable Members

        private bool _isDisposed = false;

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!this._isDisposed)
            {
                if (this._channel != null)
                {
                    this._channel.DataReceived -= Channel_DataReceived;

                    this._channel.Dispose();
                    this._channel = null;
                }

                this._session.ErrorOccured -= Session_ErrorOccured;
                this._session.Disconnected -= Session_Disconnected;

                // If disposing equals true, dispose all managed
                // and unmanaged resources.
                if (disposing)
                {
                    // Dispose managed resources.
                    if (this._errorOccuredWaitHandle != null)
                    {
                        this._errorOccuredWaitHandle.Dispose();
                        this._errorOccuredWaitHandle = null;
                    }
                    if (this._sftpVersionConfirmed != null)
                    {
                        this._sftpVersionConfirmed.Dispose();
                        this._sftpVersionConfirmed = null;
                    }
                }

                // Note disposing has been done.
                _isDisposed = true;
            }
        }

        /// <summary>
        /// Releases unmanaged resources and performs other cleanup operations before the
        /// <see cref="SftpSession"/> is reclaimed by garbage collection.
        /// </summary>
        ~SftpSession()
        {
            // Do not re-create Dispose clean-up code here.
            // Calling Dispose(false) is optimal in terms of
            // readability and maintainability.
            Dispose(false);
        }

        #endregion
    }
}
