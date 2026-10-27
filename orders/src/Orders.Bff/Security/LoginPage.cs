using System.Text.Encodings.Web;

namespace Orders.Bff.Security;

/// <summary>The development sign-in form: plain HTML, labelled fields, the antiforgery token as a hidden field.</summary>
internal static class LoginPage
{
    public static string Render(string tokenField, string token, string? returnUrl, bool failed)
    {
        var encoder = HtmlEncoder.Default;
        var error = failed
            ? """<p id="login-error" role="alert">Wrong user name or password.</p>"""
            : string.Empty;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Sign in (development)</title>
            </head>
            <body>
              <main>
                <h1>Sign in</h1>
                <p>Development sign-in. Users <code>shopper</code> and <code>admin</code>; passwords are in your <code>.env</code>.</p>
                {{error}}
                <form method="post" action="/bff/login">
                  <input name="{{encoder.Encode(tokenField)}}" type="hidden" value="{{encoder.Encode(token)}}">
                  <input name="returnUrl" type="hidden" value="{{encoder.Encode(returnUrl ?? "/")}}">
                  <p><label for="username">User name</label> <input id="username" name="username" autocomplete="username" required></p>
                  <p><label for="password">Password</label> <input id="password" name="password" type="password" autocomplete="current-password" required></p>
                  <p><button type="submit">Sign in</button></p>
                </form>
              </main>
            </body>
            </html>
            """;
    }
}
