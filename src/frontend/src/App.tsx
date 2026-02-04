import { BrowserRouter } from 'react-router-dom'
import { AppProvider } from './app/AppContext'
import LayoutShell from './features/layout/LayoutShell'
import { Toaster } from "@/components/ui/sonner"

const App = () => {
  return (
    <AppProvider>
      <BrowserRouter>
        <LayoutShell />
        <Toaster />
      </BrowserRouter>
    </AppProvider>
  )
}

export default App
