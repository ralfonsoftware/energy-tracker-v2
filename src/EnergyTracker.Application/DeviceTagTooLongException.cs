namespace EnergyTracker.Application;

// AD-20/AD-6: a smart plug file's device tag is longer than SmartPlugReading.DeviceNameMaxLength (it is
// part of the unmapped-reading key, so the column is bounded). Thrown by the vendor parsers;
// BackgroundJobProcessor maps this type to JobFailureCodes.DeviceTagTooLong, so the message below is
// logged and never reaches the client.
public class DeviceTagTooLongException(string message) : Exception(message);
