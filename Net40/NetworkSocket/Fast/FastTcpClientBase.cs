﻿using NetworkSocket.Fast.Context;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace NetworkSocket.Fast
{
    /// <summary>
    /// 快速构建Tcp客户端抽象类
    /// </summary>
    public abstract class FastTcpClientBase : TcpClientBase<FastPacket, FastPacket>, IFastTcpClient
    {
        /// <summary>
        /// 所有服务行为
        /// </summary>
        private FastActionList fastActionList;

        /// <summary>
        /// 数据包哈希码提供者
        /// </summary>
        private HashCodeProvider hashCodeProvider;

        /// <summary>
        /// 任务行为表
        /// </summary>
        private TaskSetActionTable taskSetActionTable;

        /// <summary>
        /// 获取或设置请求等待超时时间(毫秒) 
        /// 默认30秒
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public int TimeOut
        {
            get
            {
                return this.taskSetActionTable.TimeOut;
            }
            set
            {
                this.taskSetActionTable.TimeOut = value;
            }
        }

        /// <summary>
        /// 获取或设置序列化工具
        /// 默认是Json序列化
        /// </summary>
        public ISerializer Serializer { get; set; }

        /// <summary>
        /// 快速构建Tcp服务端
        /// </summary>
        public FastTcpClientBase()
        {
            this.fastActionList = new FastActionList(FastTcpCommon.GetServiceFastActions(this.GetType()));
            this.hashCodeProvider = new HashCodeProvider();
            this.taskSetActionTable = new TaskSetActionTable();
            this.Serializer = new DefaultSerializer();
        }

        /// <summary>
        /// 当接收到远程端的数据时，将触发此方法
        /// 此方法用于处理和分析收到的数据
        /// 如果得到一个数据包，将触发OnRecvComplete方法
        /// [注]这里只需处理一个数据包的流程
        /// </summary>
        /// <param name="builder">接收到的历史数据</param>
        /// <returns>如果不够一个数据包，则请返回null</returns>
        protected override FastPacket OnReceive(ByteBuilder builder)
        {
            return FastPacket.From(builder);
        }

        /// <summary>
        /// 当接收到服务发来的数据包时，将触发此方法
        /// </summary>
        /// <param name="tRecv">接收到的数据类型</param>
        protected override void OnRecvComplete(FastPacket tRecv)
        {
            var requestContext = new RequestContext { Client = this, Packet = tRecv };
            if (tRecv.IsException == false)
            {
                this.ProcessRequest(requestContext);
            }
            else
            {
                this.ProcessRemoteException(requestContext);
            }
        }

        /// <summary>
        /// 处理远返回的程异常
        /// </summary>
        /// <param name="requestContext">请求上下文</param>
        private void ProcessRemoteException(RequestContext requestContext)
        {
            var remoteException = FastTcpCommon.SetFastActionTaskException(this.Serializer, this.taskSetActionTable, requestContext);
            if (remoteException == null)
            {
                return;
            }

            var exceptionContext = new ExceptionContext(requestContext, remoteException);
            this.OnException(exceptionContext);

            if (exceptionContext.ExceptionHandled == false)
            {
                throw exceptionContext.Exception;
            }
        }

        /// <summary>
        /// 处理正常的数据请求
        /// </summary>      
        /// <param name="requestContext">请求上下文</param>
        private void ProcessRequest(RequestContext requestContext)
        {
            var action = this.GetFastAction(requestContext);
            if (action == null)
            {
                return;
            }

            var actionContext = new ActionContext(requestContext, action);
            if (action.Implement == Implements.Self)
            {
                this.TryExecuteAction(actionContext);
            }
            else
            {
                FastTcpCommon.SetFastActionTaskResult(actionContext, this.taskSetActionTable);
            }
        }

        /// <summary>
        /// 获取服务行为
        /// </summary>
        /// <param name="requestContext">请求上下文</param>
        /// <returns></returns>
        private FastAction GetFastAction(RequestContext requestContext)
        {
            var action = this.fastActionList.TryGet(requestContext.Packet.Command);
            if (action != null)
            {
                return action;
            }

            var exception = new ActionNotImplementException(requestContext.Packet.Command);
            var exceptionContext = new ExceptionContext(requestContext, exception);

            FastTcpCommon.SetRemoteException(this.Serializer, exceptionContext);
            this.OnException(exceptionContext);

            if (exceptionContext.ExceptionHandled == false)
            {
                throw exception;
            }

            return null;
        }


        /// <summary>
        /// 调用自身方法
        /// 将返回值发送给服务器
        /// 或将异常发送给服务器
        /// </summary>    
        /// <param name="actionContext">上下文</param>       
        private void TryExecuteAction(ActionContext actionContext)
        {
            try
            {
                this.ExecuteAction(actionContext);
            }
            catch (AggregateException exception)
            {
                foreach (var inner in exception.InnerExceptions)
                {
                    this.ProcessExecutingException(actionContext, inner);
                }
            }
            catch (Exception exception)
            {
                this.ProcessExecutingException(actionContext, exception);
            }
        }


        /// <summary>
        /// 执行服务行为
        /// </summary>
        /// <param name="actionContext">上下文</param>   
        private void ExecuteAction(ActionContext actionContext)
        {
            var parameters = FastTcpCommon.GetFastActionParameters(this.Serializer, actionContext);
            var returnValue = actionContext.Action.Execute(this, parameters);
            if (actionContext.Action.IsVoidReturn == false && this.IsConnected)
            {
                actionContext.Packet.SetBodyBinary(this.Serializer, returnValue);
                this.Send(actionContext.Packet);
            }
        }

        /// <summary>
        /// 处理服务行为执行过程中产生的异常
        /// </summary>
        /// <param name="actionContext">上下文</param>       
        /// <param name="exception">异常项</param>
        private void ProcessExecutingException(ActionContext actionContext, Exception exception)
        {
            var exceptionContext = new ExceptionContext(actionContext, new ActionException(actionContext, exception));
            FastTcpCommon.SetRemoteException(this.Serializer, exceptionContext);
            this.OnException(exceptionContext);

            if (exceptionContext.ExceptionHandled == false)
            {
                throw exception;
            }
        }

        /// <summary>
        /// 当操作中遇到处理异常时，将触发此方法
        /// </summary>      
        /// <param name="filterContext">上下文</param>
        protected virtual void OnException(ExceptionContext filterContext)
        {
        }

        /// <summary>
        /// 调用服务端实现的服务方法        
        /// </summary>       
        /// <param name="command">服务行为的command值</param>
        /// <param name="parameters">参数列表</param>   
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="SocketException"></exception> 
        public Task InvokeRemote(int command, params object[] parameters)
        {
            return Task.Factory.StartNew(() =>
            {
                var packet = new FastPacket(command, this.hashCodeProvider.GetHashCode());
                packet.SetBodyBinary(this.Serializer, parameters);
                this.Send(packet);
            });
        }

        /// <summary>
        /// 调用服务端实现的服务方法    
        /// 并返回结果数据任务
        /// </summary>
        /// <typeparam name="T">返回值类型</typeparam>
        /// <param name="command">服务行为的command值</param>
        /// <param name="parameters">参数</param>          
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="SocketException"></exception> 
        /// <exception cref="RemoteException"></exception>
        /// <exception cref="TimeoutException"></exception>
        /// <returns>远程数据任务</returns>    
        public Task<T> InvokeRemote<T>(int command, params object[] parameters)
        {
            return FastTcpCommon.InvokeRemote<T>(this, this.taskSetActionTable, this.Serializer, command, this.hashCodeProvider.GetHashCode(), parameters);
        }

        /// <summary>
        /// 当与服务器断开连接时，将触发此方法
        /// 并触发未完成的请求产生SocketException异常
        /// </summary>
        protected override void OnDisconnect()
        {
            var taskSetActions = this.taskSetActionTable.TakeAll();
            foreach (var taskSetAction in taskSetActions)
            {
                taskSetAction.SetAction(SetTypes.SetShutdownException, null);
            }
        }

        #region IDisponse
        /// <summary>
        /// 释放资源
        /// </summary>
        /// <param name="disposing">是否也释放托管资源</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                this.fastActionList = null;

                this.taskSetActionTable.Clear();
                this.taskSetActionTable = null;

                this.hashCodeProvider = null;
                this.Serializer = null;
            }
        }
        #endregion
    }
}
