
using RtspMulticaster;
using System;

NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

_logger.Info("Starting");
RtspServer monServeur = new(8554);

monServeur.StartListen();
RTSPDispatcher.Instance.StartQueue();

while (Console.ReadLine() != "q")
{
}

monServeur.StopListen();
RTSPDispatcher.Instance.StopQueue();
