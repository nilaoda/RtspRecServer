import { useEffect, useState } from "react"
import { type Theme, ThemeProviderContext } from "./theme-context"

type ThemeProviderProps = {
  children: React.ReactNode
  defaultTheme?: Theme
  storageKey?: string
}

export function ThemeProvider({
  children,
  defaultTheme = "system",
  storageKey = "vite-ui-theme",
}: ThemeProviderProps) {
  const [theme, setTheme] = useState<Theme>(() => {
    const stored = localStorage.getItem(storageKey)
    return stored === "light" || stored === "dark" || stored === "system"
      ? stored
      : defaultTheme
  })

  useEffect(() => {
    const root = window.document.documentElement
    const mediaQuery = window.matchMedia("(prefers-color-scheme: dark)")

    const applyTheme = () => {
      const resolvedTheme = theme === "system"
        ? (mediaQuery.matches ? "dark" : "light")
        : theme

      root.classList.remove("light", "dark")
      root.classList.add(resolvedTheme)
      root.style.colorScheme = resolvedTheme
    }

    applyTheme()

    if (theme !== "system") {
      return
    }

    mediaQuery.addEventListener("change", applyTheme)
    return () => mediaQuery.removeEventListener("change", applyTheme)
  }, [theme])

  const value = {
    theme,
    setTheme: (theme: Theme) => {
      localStorage.setItem(storageKey, theme)
      setTheme(theme)
    },
  }

  return (
    <ThemeProviderContext.Provider {...{ value }}>
      {children}
    </ThemeProviderContext.Provider>
  )
}
