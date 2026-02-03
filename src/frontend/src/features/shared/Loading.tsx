import React from 'react';
import { Spin } from 'antd';
import { LoadingOutlined } from '@ant-design/icons';

interface LoadingProps {
  tip?: string;
  fullPage?: boolean;
}

const Loading: React.FC<LoadingProps> = ({ tip = '加载中...', fullPage = false }) => {
  const antIcon = <LoadingOutlined style={{ fontSize: 24 }} spin />;

  const content = (
    <div style={{
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      justifyContent: 'center',
      padding: '40px 0',
      width: '100%',
      height: fullPage ? '100vh' : '100%',
      minHeight: fullPage ? 'auto' : '200px'
    }}>
      <Spin indicator={antIcon} tip={tip} />
    </div>
  );

  if (fullPage) {
    return (
      <div style={{
        position: 'fixed',
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        background: 'rgba(255, 255, 255, 0.8)',
        zIndex: 9999,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center'
      }}>
        {content}
      </div>
    );
  }

  return content;
};

export default Loading;
