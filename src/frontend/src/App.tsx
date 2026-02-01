import { BrowserRouter } from 'react-router-dom'
import { App as AntdApp, ConfigProvider, theme } from 'antd'
import { AppProvider } from './app/AppContext'
import { useAppContext } from './app/context'
import { AntdMessageBridge } from './app/antdApp'
import LayoutShell from './features/layout/LayoutShell'

const AppShell = () => {
  const { themeMode } = useAppContext()
  return (
    <ConfigProvider
      theme={{
        algorithm: themeMode === 'dark' ? theme.darkAlgorithm : theme.defaultAlgorithm,
      }}
    >
      <AntdApp>
        <AntdMessageBridge />
        <LayoutShell />
      </AntdApp>
    </ConfigProvider>
  )
}

const App = () => {
  return (
    <AppProvider>
      <BrowserRouter>
        <AppShell />
      </BrowserRouter>
    </AppProvider>
  )
}

export default App
