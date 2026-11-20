namespace Managerr.Indexers.Cardigann;

public class CardigannEngineException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class CardigannConfigException(string message, Exception? innerException = null)
    : CardigannEngineException(message, innerException);

public sealed class CardigannLoginException(string message, Exception? innerException = null)
    : CardigannEngineException(message, innerException);

public sealed record CardigannFilterSpec(
    string Name,
    IReadOnlyList<string> Args);

public sealed record CardigannFieldSpec(
    string? Selector = null,
    string? Attribute = null,
    string? Remove = null,
    string? Text = null,
    string? DefaultValue = null,
    bool Optional = false,
    IReadOnlyList<KeyValuePair<string, string>>? CaseMap = null,
    IReadOnlyList<CardigannFilterSpec>? Filters = null);

public sealed record CardigannRowsSpec(
    string Selector = "table tbody tr",
    string? Attribute = null,
    int After = 0,
    bool MissingAttributeEqualsNoResults = false,
    CardigannFieldSpec? DateHeaders = null,
    CardigannFieldSpec? Count = null,
    IReadOnlyList<CardigannFilterSpec>? Filters = null);

public sealed record CardigannLoginErrorSpec(
    string? Path,
    string? Selector,
    CardigannFieldSpec? Message);

public sealed record CardigannLoginTestSpec(
    string? Path,
    string? Selector);

public sealed record CardigannCaptchaSpec(
    string Type,
    string? Selector,
    string? Input);

public sealed record CardigannLoginSpec(
    string? Path,
    string? SubmitPath,
    string Method,
    string? FormSelector,
    IReadOnlyList<string> Cookies,
    IReadOnlyList<KeyValuePair<string, string>> Inputs,
    IReadOnlyDictionary<string, CardigannFieldSpec> SelectorInputs,
    IReadOnlyDictionary<string, CardigannFieldSpec> GetSelectorInputs,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<CardigannLoginErrorSpec> Errors,
    CardigannLoginTestSpec? Test,
    CardigannCaptchaSpec? Captcha);

public sealed record CardigannSearchPathSpec(
    string Path,
    string Method = "get",
    IReadOnlyList<string>? Categories = null,
    bool InheritInputs = true,
    bool FollowRedirect = true,
    IReadOnlyList<KeyValuePair<string, string>>? Inputs = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string ResponseType = "html",
    string? NoResultsMessage = null,
    CardigannRowsSpec? RowsOverride = null,
    IReadOnlyList<KeyValuePair<string, CardigannFieldSpec>>? FieldsOverride = null);

public sealed record CardigannSearchSpec(
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<CardigannFilterSpec> KeywordsFilters,
    IReadOnlyList<KeyValuePair<string, string>> Inputs,
    IReadOnlyList<CardigannSearchPathSpec> Paths,
    CardigannRowsSpec Rows,
    IReadOnlyList<KeyValuePair<string, CardigannFieldSpec>> Fields,
    IReadOnlyList<CardigannLoginErrorSpec> Errors);

public sealed record CardigannCategoryMapping(
    string TrackerId,
    int NewznabCategory,
    string? Description = null,
    bool IsDefault = false);
