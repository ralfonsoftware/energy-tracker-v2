namespace EnergyTracker.Application;

// Story 11.10, AD-6/AD-9 (audit S4): a spreadsheet package decompresses past the parser's limits (a zip
// bomb, or a sheet with more rows than any real export holds). Thrown by the vendor parser;
// BackgroundJobProcessor maps this type to JobFailureCodes.SpreadsheetTooLarge, so the message below is
// logged and never reaches the client.
public class SpreadsheetTooLargeException(string message) : Exception(message);
