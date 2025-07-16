﻿namespace VpnHood.Core.Client.VpnServices.Manager.Exceptions;

public class VpnServiceNotReadyException : Exception
{
    public VpnServiceNotReadyException(string message) :
        base(message)
    { }

    public VpnServiceNotReadyException(string message, Exception innerException) :
        base(message, innerException)
    { }

}