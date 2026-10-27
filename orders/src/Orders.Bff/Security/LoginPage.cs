using System.Security.Cryptography;
using System.Text.Encodings.Web;

namespace Orders.Bff.Security;

/// <summary>
/// The development sign-in form: plain HTML with its own stylesheet and a few lines of script (show the password, a busy
/// button), labelled fields and the antiforgery token as a hidden field. The form works without the script.
/// </summary>
internal static class LoginPage
{
    /// <summary>Where the page's own files are served: <c>/bff/login/{file}</c>.</summary>
    public const string AssetsPath = "/bff/login";

    /// <summary>
    /// The page's files, embedded in the assembly: the Content-Security-Policy nginx sends lets the page load styles and
    /// scripts only from this site, never inline. The CSS reaches the background picture (price tags spelling "SALE")
    /// by a relative URL.
    /// </summary>
    public static IReadOnlyDictionary<string, LoginAsset> Assets { get; } = new Dictionary<string, LoginAsset>
    {
        ["background.webp"] = LoginAsset.Read("login-background.webp", "image/webp"),
        ["login.css"] = LoginAsset.Read("login.css", "text/css; charset=utf-8"),
        ["login.js"] = LoginAsset.Read("login.js", "text/javascript; charset=utf-8"),
    };

    // The content's hash in the URL, so a browser that keeps the files for a day still gets new ones after a deploy.
    private static readonly string _stylesPath = $"{AssetsPath}/login.css?v={Assets["login.css"].Version}";
    private static readonly string _scriptPath = $"{AssetsPath}/login.js?v={Assets["login.js"].Version}";

    public static string Render(string tokenField, string token, string? returnUrl, bool failed, string? userName = null)
    {
        var encoder = HtmlEncoder.Default;
        // After a wrong password the user name stays and the password gets the focus, so only the password is typed again.
        var invalid = failed ? " aria-invalid=\"true\" aria-describedby=\"login-error\"" : string.Empty;
        var userFocus = failed ? string.Empty : " autofocus";
        var passwordFocus = failed ? " autofocus" : string.Empty;
        var error = failed
            ? """
              <div id="login-error" class="error" role="alert">
                    <svg class="icon" aria-hidden="true" viewBox="0 0 16 16"><path d="M16 8A8 8 0 1 1 0 8a8 8 0 0 1 16 0M8 4a.905.905 0 0 0-.9.995l.35 3.507a.552.552 0 0 0 1.1 0l.35-3.507A.905.905 0 0 0 8 4m.002 6a1 1 0 1 0 0 2 1 1 0 0 0 0-2"/></svg>
                    <span>Usuário ou senha incorretos. Confira e tente de novo.</span>
                  </div>
              """
            : string.Empty;

        return $$"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="color-scheme" content="light">
              <title>Entrar · Pedidos e Catálogo</title>
              <link rel="stylesheet" href="{{_stylesPath}}">
            </head>
            <body>
              <div class="page">
                <header class="brand">
                  <span class="brand-mark" aria-hidden="true"><svg class="icon" viewBox="0 0 16 16"><path d="M8.186 1.113a.5.5 0 0 0-.372 0L1.846 3.5l2.404.961L10.404 2zm3.564 1.426L5.596 5 8 5.961 14.154 3.5zm3.25 1.7-6.5 2.6v7.922l6.5-2.6V4.24zM7.5 14.762V6.838L1 4.239v7.923zM7.443.184a1.5 1.5 0 0 1 1.114 0l7.129 2.852A.5.5 0 0 1 16 3.5v8.662a1 1 0 0 1-.629.928l-7.185 2.874a.5.5 0 0 1-.372 0L.63 13.09a1 1 0 0 1-.63-.928V3.5a.5.5 0 0 1 .314-.464z"/></svg></span>
                  <p class="brand-name">Pedidos e Catálogo</p>
                  <p class="brand-tagline">Entre para comprar na loja ou administrar pedidos, regras e catálogo.</p>
                </header>
                <main class="card">
                  <h1>Entrar</h1>
                  <p class="lead">Login de desenvolvimento: usuários <code>shopper</code> e <code>admin</code>; as senhas estão no seu <code>.env</code>.</p>
                  {{error}}
                  <form id="login-form" method="post" action="/bff/login">
                    <input name="{{encoder.Encode(tokenField)}}" type="hidden" value="{{encoder.Encode(token)}}">
                    <input name="returnUrl" type="hidden" value="{{encoder.Encode(returnUrl ?? string.Empty)}}">
                    <div class="field">
                      <label for="username">Usuário</label>
                      <input id="username" name="username" value="{{encoder.Encode(userName ?? string.Empty)}}" autocomplete="username" autocapitalize="none" spellcheck="false" required{{invalid}}{{userFocus}}>
                    </div>
                    <div class="field">
                      <label for="password">Senha</label>
                      <div class="control">
                        <input id="password" name="password" type="password" autocomplete="current-password" required{{invalid}}{{passwordFocus}}>
                        <button id="reveal" class="reveal" type="button" aria-label="Mostrar senha" aria-pressed="false" aria-controls="password" hidden>
                          <svg class="icon eye" aria-hidden="true" viewBox="0 0 16 16"><path d="M16 8s-3-5.5-8-5.5S0 8 0 8s3 5.5 8 5.5S16 8 16 8M1.173 8a13 13 0 0 1 1.66-2.043C4.12 4.668 5.88 3.5 8 3.5s3.879 1.168 5.168 2.457A13 13 0 0 1 14.828 8q-.086.13-.195.288c-.335.48-.83 1.12-1.465 1.755C11.879 11.332 10.119 12.5 8 12.5s-3.879-1.168-5.168-2.457A13 13 0 0 1 1.172 8z"/><path d="M8 5.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5M4.5 8a3.5 3.5 0 1 1 7 0 3.5 3.5 0 0 1-7 0"/></svg>
                          <svg class="icon eye-slash" aria-hidden="true" viewBox="0 0 16 16" hidden><path d="M13.359 11.238C15.06 9.72 16 8 16 8s-3-5.5-8-5.5a7 7 0 0 0-2.79.588l.77.771A6 6 0 0 1 8 3.5c2.12 0 3.879 1.168 5.168 2.457A13 13 0 0 1 14.828 8q-.086.13-.195.288c-.335.48-.83 1.12-1.465 1.755q-.247.248-.517.486z"/><path d="M11.297 9.176a3.5 3.5 0 0 0-4.474-4.474l.823.823a2.5 2.5 0 0 1 2.829 2.829zm-2.943 1.299.822.822a3.5 3.5 0 0 1-4.474-4.474l.823.823a2.5 2.5 0 0 0 2.829 2.829"/><path d="M3.35 5.47q-.27.24-.518.487A13 13 0 0 0 1.172 8l.195.288c.335.48.83 1.12 1.465 1.755C4.121 11.332 5.881 12.5 8 12.5c.716 0 1.39-.133 2.02-.36l.77.772A7 7 0 0 1 8 13.5C3 13.5 0 8 0 8s.939-1.721 2.641-3.238l.708.709zm10.296 8.884-12-12 .708-.708 12 12z"/></svg>
                        </button>
                      </div>
                    </div>
                    <div class="actions">
                      <button class="button primary" type="submit">
                        <svg class="icon" aria-hidden="true" viewBox="0 0 16 16"><path fill-rule="evenodd" d="M6 3.5a.5.5 0 0 1 .5-.5h8a.5.5 0 0 1 .5.5v9a.5.5 0 0 1-.5.5h-8a.5.5 0 0 1-.5-.5v-2a.5.5 0 0 0-1 0v2A1.5 1.5 0 0 0 6.5 14h8a1.5 1.5 0 0 0 1.5-1.5v-9A1.5 1.5 0 0 0 14.5 2h-8A1.5 1.5 0 0 0 5 3.5v2a.5.5 0 0 0 1 0z"/><path fill-rule="evenodd" d="M11.854 8.354a.5.5 0 0 0 0-.708l-3-3a.5.5 0 1 0-.708.708L10.293 7.5H1.5a.5.5 0 0 0 0 1h8.793l-2.147 2.146a.5.5 0 0 0 .708.708z"/></svg>
                        <span id="submit-text">Entrar</span>
                      </button>
                    </div>
                  </form>
                  <p class="divider">ou</p>
                  <a class="button secondary" href="/shop.html">
                    <svg class="icon" aria-hidden="true" viewBox="0 0 16 16"><path d="M2.97 1.35A1 1 0 0 1 3.73 1h8.54a1 1 0 0 1 .76.35l2.609 3.044A1.5 1.5 0 0 1 16 5.37v.255a2.375 2.375 0 0 1-4.25 1.458A2.37 2.37 0 0 1 9.875 8 2.37 2.37 0 0 1 8 7.083 2.37 2.37 0 0 1 6.125 8a2.37 2.37 0 0 1-1.875-.917A2.375 2.375 0 0 1 0 5.625V5.37a1.5 1.5 0 0 1 .361-.976zm1.78 4.275a1.375 1.375 0 0 0 2.75 0 .5.5 0 0 1 1 0 1.375 1.375 0 0 0 2.75 0 .5.5 0 0 1 1 0 1.375 1.375 0 1 0 2.75 0V5.37a.5.5 0 0 0-.12-.325L12.27 2H3.73L1.12 5.045A.5.5 0 0 0 1 5.37v.255a1.375 1.375 0 0 0 2.75 0 .5.5 0 0 1 1 0M1.5 8.5A.5.5 0 0 1 2 9v6h1v-5a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v5h6V9a.5.5 0 0 1 1 0v6h.5a.5.5 0 0 1 0 1H.5a.5.5 0 0 1 0-1H1V9a.5.5 0 0 1 .5-.5M4 15h3v-5H4zm5-5a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1h-2a1 1 0 0 1-1-1zm3 0h-2v3h2z"/></svg>
                    Ver a loja sem entrar
                  </a>
                  <p class="note">Login provisório, só para desenvolvimento; a versão final usará um provedor de identidade.</p>
                </main>
              </div>
              <script src="{{_scriptPath}}" defer></script>
            </body>
            </html>
            """;
    }
}

/// <summary>One file of the sign-in page, with the short hash of its content that versions its URL.</summary>
internal sealed record LoginAsset(byte[] Content, string ContentType)
{
    public string Version { get; } = Convert.ToHexStringLower(SHA256.HashData(Content))[..12];

    public static LoginAsset Read(string resource, string contentType)
    {
        using var stream = typeof(LoginAsset).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The {resource} resource is missing from the assembly.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        return new LoginAsset(copy.ToArray(), contentType);
    }
}
