module Components.Component

open Fable.Core
open React
open type React.DSL.DOMProps

[<ExportDefault>]
let Component () : JSX.Element = div [] [ str "React rocks!" ]
